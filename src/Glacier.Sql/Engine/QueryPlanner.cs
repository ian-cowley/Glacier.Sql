using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
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
            return PlanQueryAsync(select, inserted, deleted).GetAwaiter().GetResult();
        }

        public async Task<LazyFrame> PlanQueryAsync(SelectStatement select, DataFrame? inserted = null, DataFrame? deleted = null)
        {
            var materializedSubqueries = new Dictionary<object, object?>();

            if (select.From == null)
            {
                var dummyCols = new List<ISeries> { new Int32Series("dummy", new[] { 1 }) };
                var dummyDf = new DataFrame(dummyCols);
                var lazy = dummyDf.Lazy();

                var clonedDummyProjs = new List<SelectItem>(select.Projections.Count);
                foreach (var p in select.Projections)
                {
                    var (rewrittenExpr, _) = await MaterializeSubqueriesInExpressionAsync(
                        p.Expression,
                        activeTables: null,
                        currentLazy: null,
                        inserted,
                        deleted,
                        materializedSubqueries,
                        subqueryCounter: new SubqueryCounter()).ConfigureAwait(false);
                    clonedDummyProjs.Add(new SelectItem(rewrittenExpr, p.Alias));
                }

                var dummyProj = clonedDummyProjs
                    .Select(p => CompileExpression(p.Expression, null, false, inserted, deleted, materializedSubqueries).Alias(p.Alias ?? "result"))
                    .ToArray();
                return lazy.Select(dummyProj);
            }

            // 1. Resolve FROM clause
            if (select.From is not SqlTableSource source)
            {
                throw new NotSupportedException("Only standard table sources are supported in FROM clause currently.");
            }

            var (tableMeta, currentLazy) = await ResolveTableSourceAsync(source.TableName, inserted, deleted, isJoinedTable: false).ConfigureAwait(false);

            var activeTables = new List<ActiveTableInfo>
            {
                CreateIdentityActiveTable(source.TableName, source.Alias, tableMeta)
            };

            // Track exact physical column names in currentLazy as joins progress
            var usedPhysicalCols = new HashSet<string>(tableMeta.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var orderedPhysicalCols = new List<string>(tableMeta.Columns.Select(c => c.Name));

            // 2. Process JOINS
            for (int joinHop = 0; joinHop < select.Joins.Count; joinHop++)
            {
                var join = select.Joins[joinHop];
                if (join.Table is not SqlTableSource joinSource)
                {
                    throw new NotSupportedException("Only standard table sources are supported in JOIN clause.");
                }

                var (joinTableMeta, joinLazy) = await ResolveTableSourceAsync(joinSource.TableName, inserted, deleted, isJoinedTable: true).ConfigureAwait(false);
                var leftTables = new List<ActiveTableInfo>(activeTables);
                var rightTableList = new List<ActiveTableInfo> { CreateIdentityActiveTable(joinSource.TableName, joinSource.Alias, joinTableMeta) };

                // Allocate globally unique physical names for every column in the joined table BEFORE joining
                var rightPhysMap = new Dictionary<string, string>(joinTableMeta.Columns.Count, StringComparer.OrdinalIgnoreCase);
                var rightPhysOrdered = new List<string>(joinTableMeta.Columns.Count);
                bool anyRightRenamed = false;

                foreach (var col in joinTableMeta.Columns)
                {
                    string physName = AllocateUniquePhysicalName(col.Name, usedPhysicalCols);
                    rightPhysMap[col.Name] = physName;
                    rightPhysOrdered.Add(physName);
                    if (!physName.Equals(col.Name, StringComparison.Ordinal))
                    {
                        anyRightRenamed = true;
                    }
                }

                var rightActiveInfo = new ActiveTableInfo(joinSource.TableName, joinSource.Alias, joinTableMeta, rightPhysMap);
                activeTables.Add(rightActiveInfo);

                var leftOrderedCols = new List<string>(orderedPhysicalCols);
                string joinKind = join.JoinType.ToUpperInvariant();
                string? leftKeyPhys = null;
                string? rightKeyOrig = null;

                if (joinKind != "CROSS")
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

                    (leftKeyPhys, rightKeyOrig) = ResolveJoinKeysBidirectional(leftCol, rightCol, leftTables, rightTableList);
                }

                // Collect both sides once so 0-row inputs (empty tables or empty intermediate joins)
                // never hit Polaris 1.2.6 ApplyJoin/DataFrame.Concat's 0-column empty DataFrame crash.
                var currentDf = await currentLazy.Collect().ConfigureAwait(false);
                var joinDf = await joinLazy.Collect().ConfigureAwait(false);

                if (currentDf.RowCount == 0 || joinDf.RowCount == 0)
                {
                    var zeroJoinDf = BuildZeroRowJoinResult(
                        currentDf,
                        joinDf,
                        leftTables,
                        joinTableMeta,
                        leftOrderedCols,
                        rightPhysMap,
                        rightPhysOrdered,
                        joinKind);
                    orderedPhysicalCols.AddRange(rightPhysOrdered);
                    currentLazy = zeroJoinDf.Lazy();
                    continue;
                }

                currentLazy = currentDf.Lazy();
                joinLazy = joinDf.Lazy();

                if (joinKind == "CROSS")
                {
                    if (anyRightRenamed)
                    {
                        joinLazy = joinLazy.Map(df => RenameColumnsNonMutating(df, rightPhysMap));
                    }

                    currentLazy = currentLazy.Join(joinLazy, on: "", JoinType.Cross);
                    orderedPhysicalCols.AddRange(rightPhysOrdered);
                }
                else if (joinKind == "RIGHT")
                {
                    // Swap operands: joinLazy.Join(currentLazy, Left) preserves all RIGHT rows.
                    // Copy both keys onto a temporary unique join key so neither side's real key column is coalesced/lost.
                    string tempJoinKey = AllocateUniqueTempJoinKey(usedPhysicalCols, joinHop);
                    currentLazy = currentLazy.Map(df => AddCopiedKeyColumn(df, leftKeyPhys!, tempJoinKey));
                    joinLazy = joinLazy.Map(df => PrepareJoinedRightFrame(df, rightPhysMap, rightKeyOrig!, tempJoinKey));

                    currentLazy = joinLazy.Join(currentLazy, on: tempJoinKey, JoinType.Left);

                    orderedPhysicalCols.AddRange(rightPhysOrdered);
                    currentLazy = currentLazy.Select(orderedPhysicalCols.Select(Expr.Col).ToArray());
                }
                else if (joinKind == "FULL")
                {
                    string tempJoinKey = AllocateUniqueTempJoinKey(usedPhysicalCols, joinHop);
                    currentLazy = currentLazy.Map(df => AddCopiedKeyColumn(df, leftKeyPhys!, tempJoinKey));
                    joinLazy = joinLazy.Map(df => PrepareJoinedRightFrame(df, rightPhysMap, rightKeyOrig!, tempJoinKey));

                    currentLazy = currentLazy.Join(joinLazy, on: tempJoinKey, JoinType.Outer);

                    orderedPhysicalCols.AddRange(rightPhysOrdered);
                    currentLazy = currentLazy.Select(orderedPhysicalCols.Select(Expr.Col).ToArray());
                }
                else
                {
                    // INNER or LEFT: leftKeyPhys is preserved on the left side; rightKeyOrig is preserved under rightPhysMap[rightKeyOrig]
                    // while a non-mutating copy named leftKeyPhys serves as the join key on the right side (coalesced/dropped by Polaris Join).
                    joinLazy = joinLazy.Map(df => PrepareJoinedRightFrame(df, rightPhysMap, rightKeyOrig!, leftKeyPhys!));

                    var jt = joinKind == "LEFT" ? JoinType.Left : JoinType.Inner;
                    currentLazy = currentLazy.Join(joinLazy, on: leftKeyPhys!, jt);
                    orderedPhysicalCols.AddRange(rightPhysOrdered);
                }
            }

            // 3. Process WHERE clause (recursive async subquery pre-pass handling EXISTS, OR EXISTS, multi-key correlations, IN/NOT IN, scalar subqueries)
            var subqueryCounter = new SubqueryCounter();
            if (select.Where != null)
            {
                var (rewrittenWhere, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                    select.Where,
                    activeTables,
                    currentLazy,
                    inserted,
                    deleted,
                    materializedSubqueries,
                    subqueryCounter).ConfigureAwait(false);

                if (updatedLazy != null)
                {
                    currentLazy = updatedLazy;
                }

                var predicate = CompileExpression(rewrittenWhere, activeTables, false, inserted, deleted, materializedSubqueries);
                currentLazy = currentLazy.Filter(predicate);
            }

            // Pre-materialize any subqueries in SELECT projections or HAVING
            var materializedProjections = new List<SelectItem>(select.Projections.Count);
            foreach (var proj in select.Projections)
            {
                var (rewrittenExpr, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                    proj.Expression,
                    activeTables,
                    currentLazy,
                    inserted,
                    deleted,
                    materializedSubqueries,
                    subqueryCounter).ConfigureAwait(false);
                if (updatedLazy != null) currentLazy = updatedLazy;
                materializedProjections.Add(new SelectItem(rewrittenExpr, proj.Alias));
            }

            SqlExpression? materializedHaving = select.Having;
            if (materializedHaving != null)
            {
                var (rewrittenHaving, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                    materializedHaving,
                    activeTables,
                    currentLazy,
                    inserted,
                    deleted,
                    materializedSubqueries,
                    subqueryCounter).ConfigureAwait(false);
                if (updatedLazy != null) currentLazy = updatedLazy;
                materializedHaving = rewrittenHaving;
            }

            var materializedOrderBy = new List<SqlOrderBy>(select.OrderBy.Count);
            foreach (var order in select.OrderBy)
            {
                var (rewrittenOrderExpr, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                    order.Expression,
                    activeTables,
                    currentLazy,
                    inserted,
                    deleted,
                    materializedSubqueries,
                    subqueryCounter).ConfigureAwait(false);
                if (updatedLazy != null) currentLazy = updatedLazy;
                materializedOrderBy.Add(new SqlOrderBy(rewrittenOrderExpr, order.Descending));
            }

            // 4. Process Grouping, Aggregations, HAVING, ORDER BY, and Final Projections
            bool isAggregateQuery = select.GroupBy.Count > 0 ||
                                    materializedProjections.Any(p => HasAggregateFunction(p.Expression)) ||
                                    (materializedHaving != null && HasAggregateFunction(materializedHaving));

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

                foreach (var proj in materializedProjections)
                {
                    ExtractAggregateCalls(proj.Expression, extractedAggs, keyToTempAlias, activeTables);
                }

                if (materializedHaving != null)
                {
                    ExtractAggregateCalls(materializedHaving, extractedAggs, keyToTempAlias, activeTables);
                }

                foreach (var order in materializedOrderBy)
                {
                    ExtractAggregateCalls(order.Expression, extractedAggs, keyToTempAlias, activeTables);
                }

                bool isGrouped = groupColumns.Length > 0;
                var aggProjections = new List<Expr>(extractedAggs.Count);
                foreach (var (_, call, tempAlias) in extractedAggs)
                {
                    var compiledAgg = CompileExpression(call, activeTables, isAggregating: true, inserted, deleted, materializedSubqueries, isGrouped: isGrouped);
                    aggProjections.Add(compiledAgg.Alias(tempAlias));
                }

                if (isGrouped)
                {
                    currentLazy = currentLazy.GroupBy(groupColumns).Agg(aggProjections.ToArray());
                }
                else if (materializedHaving != null || materializedOrderBy.Count > 0)
                {
                    var globalAggDf = await currentLazy.Select(aggProjections.ToArray()).Collect().ConfigureAwait(false);
                    currentLazy = globalAggDf.Lazy();
                }
                else
                {
                    currentLazy = currentLazy.Select(aggProjections.ToArray());
                }

                var finalProjections = new List<Expr>(materializedProjections.Count);
                var aliasExprMap = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);
                var aliasDirectColMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var proj in materializedProjections)
                {
                    string finalAlias = proj.Alias ?? (proj.Expression is SqlColumnRef cr ? cr.ColumnName : proj.Expression.ToString() ?? "result");
                    var postExpr = CompilePostAggregateExpression(proj.Expression, activeTables, keyToTempAlias, aliasExprMap: null, groupColumnsSet, inserted, deleted, materializedSubqueries);
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
                if (materializedHaving != null)
                {
                    var havingPredicate = CompilePostAggregateExpression(materializedHaving, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted, materializedSubqueries);
                    currentLazy = currentLazy.Filter(havingPredicate);
                }

                // 6a. ORDER BY on grouped/aggregated frame
                if (materializedOrderBy.Count > 0)
                {
                    var sortCols = new List<string>(materializedOrderBy.Count);
                    var sortDescs = new List<bool>(materializedOrderBy.Count);
                    var extraSortCols = new List<Expr>();

                    for (int i = 0; i < materializedOrderBy.Count; i++)
                    {
                        var order = materializedOrderBy[i];
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
                            var computedSortExpr = CompilePostAggregateExpression(order.Expression, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted, materializedSubqueries);
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
                var aliasExprMap = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);
                var aliasDirectColMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var projectedPhysicalCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenOutputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var proj in materializedProjections)
                {
                    if (proj.Expression is SqlStarRef star)
                    {
                        var tablesToExpand = (star.Prefix == null
                            ? activeTables
                            : activeTables.Where(t => (t.Alias ?? t.Name).Equals(star.Prefix, StringComparison.OrdinalIgnoreCase))).ToList();
                        if (star.Prefix != null && tablesToExpand.Count == 0)
                        {
                            throw new Exception($"Unknown table alias/prefix '{star.Prefix}' for '*'.");
                        }

                        foreach (var t in tablesToExpand)
                        {
                            foreach (var col in t.Metadata.Columns)
                            {
                                string physicalName = t.PhysicalColumns[col.Name];
                                string outputName = seenOutputNames.Add(col.Name) ? col.Name : physicalName;
                                while (!outputName.Equals(col.Name, StringComparison.OrdinalIgnoreCase) && !seenOutputNames.Add(outputName))
                                {
                                    outputName += "_";
                                }
                                projections.Add(Expr.Col(physicalName).Alias(outputName));
                                projectedPhysicalCols.Add(physicalName);
                            }
                        }
                    }
                    else
                    {
                        var compiled = CompileExpression(proj.Expression, activeTables, false, inserted, deleted, materializedSubqueries);
                        if (proj.Alias != null)
                        {
                            aliasExprMap[proj.Alias] = compiled;
                            seenOutputNames.Add(proj.Alias);
                            compiled = compiled.Alias(proj.Alias);
                            if (proj.Expression is SqlColumnRef cr)
                            {
                                string resolved = ResolveColumnName(cr, activeTables);
                                projectedPhysicalCols.Add(resolved);
                                aliasDirectColMap[proj.Alias] = resolved;
                            }
                        }
                        else if (proj.Expression is SqlColumnRef colRef)
                        {
                            string resolved = ResolveColumnName(colRef, activeTables);
                            projectedPhysicalCols.Add(resolved);
                            seenOutputNames.Add(colRef.ColumnName);
                            aliasExprMap[colRef.ColumnName] = Expr.Col(resolved);
                            aliasDirectColMap[colRef.ColumnName] = resolved;
                            compiled = compiled.Alias(colRef.ColumnName);
                        }
                        projections.Add(compiled);
                    }
                }

                // 5b. Non-aggregate HAVING evaluated BEFORE final Select projection (supports aliases and unprojected columns)
                if (materializedHaving != null)
                {
                    var havingPredicate = CompileNonAggregateWithAliases(materializedHaving, activeTables, aliasExprMap, inserted, deleted, materializedSubqueries);
                    currentLazy = currentLazy.Filter(havingPredicate);
                }

                // 6b. Non-aggregate ORDER BY evaluated BEFORE final Select projection (supports expressions & aliases while rejecting unprojected bare columns)
                if (materializedOrderBy.Count > 0)
                {
                    var sortCols = new List<string>(materializedOrderBy.Count);
                    var sortDescs = new List<bool>(materializedOrderBy.Count);
                    var extraSortCols = new List<Expr>();

                    for (int i = 0; i < materializedOrderBy.Count; i++)
                    {
                        var order = materializedOrderBy[i];
                        sortDescs.Add(order.Descending);

                        if (order.Expression is SqlColumnRef col)
                        {
                            if (col.Prefix == null && aliasDirectColMap.TryGetValue(col.ColumnName, out var directSortCol))
                            {
                                sortCols.Add(directSortCol);
                            }
                            else if (col.Prefix == null && aliasExprMap.TryGetValue(col.ColumnName, out var aliasedSortExpr))
                            {
                                string helperSort = $"__sort_{i}";
                                extraSortCols.Add(aliasedSortExpr.Alias(helperSort));
                                sortCols.Add(helperSort);
                            }
                            else
                            {
                                string resolvedCol = ResolveColumnName(col, activeTables);
                                if (!projectedPhysicalCols.Contains(resolvedCol))
                                {
                                    throw new Exception($"Column '{col}' in ORDER BY must appear in the SELECT list.");
                                }
                                sortCols.Add(resolvedCol);
                            }
                        }
                        else
                        {
                            var compiledSortExpr = CompileNonAggregateWithAliases(order.Expression, activeTables, aliasExprMap, inserted, deleted, materializedSubqueries);
                            string helperSort = $"__sort_{i}";
                            extraSortCols.Add(compiledSortExpr.Alias(helperSort));
                            sortCols.Add(helperSort);
                        }
                    }

                    if (extraSortCols.Count > 0)
                    {
                        currentLazy = currentLazy.WithColumns(extraSortCols.ToArray());
                    }

                    currentLazy = currentLazy.Sort(sortCols.ToArray(), sortDescs.ToArray());
                }

                // Final Select strips __sort_* and __subq_* helper columns
                currentLazy = currentLazy.Select(projections.ToArray());
            }

            // 7. Process TOP clause (Limit)
            if (select.Top.HasValue)
            {
                currentLazy = currentLazy.Limit(select.Top.Value);
            }

            return currentLazy;
        }

        private async Task<(TableMetadata Metadata, LazyFrame Frame)> ResolveTableSourceAsync(string tableName, DataFrame? inserted, DataFrame? deleted, bool isJoinedTable)
        {
            if (tableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
            {
                if (inserted == null)
                {
                    throw new Exception("The virtual table 'inserted' is only available inside trigger context.");
                }
                return (CreateVirtualTableMetadata("inserted", inserted), CloneDataFrameShallow(inserted).Lazy());
            }

            if (tableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                if (deleted == null)
                {
                    throw new Exception("The virtual table 'deleted' is only available inside trigger context.");
                }
                return (CreateVirtualTableMetadata("deleted", deleted), CloneDataFrameShallow(deleted).Lazy());
            }

            if (tableName.Equals("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase))
            {
                var tableNames = _catalog.ListTables().Select(t => t.TableName).ToArray();
                var s = new Utf8StringSeries("TABLE_NAME", tableNames);
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

                var sTable = new Utf8StringSeries("TABLE_NAME", tableNames.ToArray());
                var sColumn = new Utf8StringSeries("COLUMN_NAME", columnNames.ToArray());
                var sDataType = new Utf8StringSeries("DATA_TYPE", dataTypes.ToArray());
                var sNullable = new Utf8StringSeries("IS_NULLABLE", isNullables.ToArray());

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
                SelectStatement viewSelect;
                List<ColumnMetadata>? cachedSchema = null;

                if (_catalog.TryGetCachedView(tableName, viewMeta.DefinitionSql, out var cachedAst, out var cachedCols) && cachedAst != null)
                {
                    viewSelect = CloneSelectStatement(cachedAst);
                    cachedSchema = cachedCols;
                }
                else
                {
                    var lexer = new SqlLexer(viewMeta.DefinitionSql);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var parsed = (SelectStatement)parser.Parse();
                    _catalog.CacheView(tableName, viewMeta.DefinitionSql, parsed, null);
                    viewSelect = CloneSelectStatement(parsed);
                }

                var recursiveLazy = await PlanQueryAsync(viewSelect, inserted, deleted).ConfigureAwait(false);
                if (cachedSchema == null || inserted != null || deleted != null)
                {
                    var schemaDf = await recursiveLazy.Limit(0).Collect().ConfigureAwait(false);
                    cachedSchema = schemaDf.Columns.Select(c => new ColumnMetadata
                    {
                        Name = c.Name,
                        DataType = GetSqlDataType(c)
                    }).ToList();

                    if (inserted == null && deleted == null)
                    {
                        _catalog.CacheView(tableName, viewMeta.DefinitionSql, viewSelect, cachedSchema);
                    }
                }

                var meta = new TableMetadata
                {
                    TableName = tableName,
                    Columns = cachedSchema,
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
                df = CloneDataFrameShallow(entry.CurrentSnapshot);
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

        private static DataFrame BuildZeroRowJoinResult(
            DataFrame currentDf,
            DataFrame joinDf,
            List<ActiveTableInfo> leftTables,
            TableMetadata rightMeta,
            List<string> leftOrderedCols,
            Dictionary<string, string> rightPhysMap,
            List<string> rightPhysOrdered,
            string joinKind)
        {
            bool leftEmpty = currentDf.RowCount == 0;
            bool rightEmpty = joinDf.RowCount == 0;

            var leftColMetaMap = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in leftTables)
            {
                foreach (var colMeta in t.Metadata.Columns)
                {
                    if (t.PhysicalColumns.TryGetValue(colMeta.Name, out var physName) && !leftColMetaMap.ContainsKey(physName))
                    {
                        leftColMetaMap[physName] = colMeta;
                    }
                }
            }

            var rightColMetaByPhys = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var colMeta in rightMeta.Columns)
            {
                if (rightPhysMap.TryGetValue(colMeta.Name, out var physName))
                {
                    rightColMetaByPhys[physName] = colMeta;
                }
            }

            bool preserveLeft = !leftEmpty && (joinKind == "LEFT" || joinKind == "FULL");
            bool preserveRight = !rightEmpty && (joinKind == "RIGHT" || joinKind == "FULL");

            var resultCols = new List<ISeries>(leftOrderedCols.Count + rightPhysOrdered.Count);

            if (preserveLeft)
            {
                int rowCount = currentDf.RowCount;
                foreach (var leftPhys in leftOrderedCols)
                {
                    ISeries? existing = FindColumnIgnoreCase(currentDf, leftPhys);
                    if (existing != null)
                    {
                        resultCols.Add(CloneSeriesWithName(existing, leftPhys));
                    }
                    else
                    {
                        leftColMetaMap.TryGetValue(leftPhys, out var meta);
                        resultCols.Add(CreateTypedNullSeries(null, meta, leftPhys, rowCount));
                    }
                }

                foreach (var rightColMeta in rightMeta.Columns)
                {
                    string rightPhys = rightPhysMap[rightColMeta.Name];
                    ISeries? sample = FindColumnIgnoreCase(joinDf, rightColMeta.Name);
                    resultCols.Add(CreateTypedNullSeries(sample, rightColMeta, rightPhys, rowCount));
                }

                return new DataFrame(resultCols);
            }

            if (preserveRight)
            {
                int rowCount = joinDf.RowCount;
                foreach (var leftPhys in leftOrderedCols)
                {
                    ISeries? sample = FindColumnIgnoreCase(currentDf, leftPhys);
                    leftColMetaMap.TryGetValue(leftPhys, out var meta);
                    resultCols.Add(CreateTypedNullSeries(sample, meta, leftPhys, rowCount));
                }

                foreach (var rightColMeta in rightMeta.Columns)
                {
                    string rightPhys = rightPhysMap[rightColMeta.Name];
                    ISeries? srcCol = FindColumnIgnoreCase(joinDf, rightColMeta.Name);
                    if (srcCol == null)
                    {
                        resultCols.Add(CreateTypedNullSeries(null, rightColMeta, rightPhys, rowCount));
                    }
                    else
                    {
                        resultCols.Add(CloneSeriesWithName(srcCol, rightPhys));
                    }
                }

                return new DataFrame(resultCols);
            }

            // INNER, CROSS, LEFT (left empty), RIGHT (right empty), FULL (both empty) -> 0-row typed DataFrame
            foreach (var leftPhys in leftOrderedCols)
            {
                ISeries? sample = FindColumnIgnoreCase(currentDf, leftPhys);
                leftColMetaMap.TryGetValue(leftPhys, out var meta);
                resultCols.Add(CreateTypedNullSeries(sample, meta, leftPhys, 0));
            }

            foreach (var rightColMeta in rightMeta.Columns)
            {
                string rightPhys = rightPhysMap[rightColMeta.Name];
                ISeries? sample = FindColumnIgnoreCase(joinDf, rightColMeta.Name);
                resultCols.Add(CreateTypedNullSeries(sample, rightColMeta, rightPhys, 0));
            }

            return new DataFrame(resultCols);
        }

        private static ISeries CreateTypedNullSeries(ISeries? sampleCol, ColumnMetadata? fallbackMeta, string targetName, int length)
        {
            if (length == 0)
            {
                if (sampleCol != null)
                {
                    if (sampleCol is Utf8StringSeries)
                    {
                        return new Utf8StringSeries(targetName, 0, 0);
                    }
                    var empty = sampleCol.CloneEmpty(0);
                    empty.Rename(targetName);
                    return empty;
                }
            }

            if (sampleCol != null)
            {
                if (sampleCol is Utf8StringSeries)
                {
                    var emptyStrings = new string[length];
                    Array.Fill(emptyStrings, string.Empty);
                    var s = new Utf8StringSeries(targetName, emptyStrings);
                    for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                    return s;
                }

                var typed = sampleCol.CloneEmpty(length);
                typed.Rename(targetName);
                for (int i = 0; i < length; i++) typed.ValidityMask.SetNull(i);
                return typed;
            }

            string dt = fallbackMeta?.DataType.ToUpperInvariant() ?? "INT";
            if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
            {
                if (length == 0) return new Utf8StringSeries(targetName, 0, 0);
                var emptyStrings = new string[length];
                Array.Fill(emptyStrings, string.Empty);
                var s = new Utf8StringSeries(targetName, emptyStrings);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
            {
                var s = new Float64Series(targetName, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "BIT" || dt == "BOOLEAN")
            {
                var s = new BooleanSeries(targetName, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "DATETIME" || dt == "DATE")
            {
                var s = new TimeSeries(targetName, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }

            var intSeries = new Int32Series(targetName, length);
            for (int i = 0; i < length; i++) intSeries.ValidityMask.SetNull(i);
            return intSeries;
        }

        private static string AllocateUniquePhysicalName(string baseName, HashSet<string> usedNames)
        {
            if (usedNames.Add(baseName))
            {
                return baseName;
            }

            string candidate = $"{baseName}_right";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }

            int suffix = 2;
            while (true)
            {
                candidate = $"{baseName}_right_{suffix}";
                if (usedNames.Add(candidate))
                {
                    return candidate;
                }
                suffix++;
            }
        }

        private static string AllocateUniqueTempJoinKey(HashSet<string> usedNames, int joinHop)
        {
            string candidate = $"__jk_{joinHop}";
            while (usedNames.Contains(candidate))
            {
                candidate += "_";
            }
            return candidate;
        }

        private static DataFrame RenameColumnsNonMutating(DataFrame df, Dictionary<string, string> targetNames)
        {
            var newCols = new List<ISeries>(df.Columns.Count);
            foreach (var col in df.Columns)
            {
                if (targetNames.TryGetValue(col.Name, out string? targetName) &&
                    !col.Name.Equals(targetName, StringComparison.Ordinal))
                {
                    newCols.Add(CloneSeriesWithName(col, targetName));
                }
                else
                {
                    newCols.Add(col);
                }
            }
            return new DataFrame(newCols);
        }

        private static DataFrame AddCopiedKeyColumn(DataFrame df, string sourceKeyName, string targetKeyName)
        {
            ISeries? keyCol = FindColumnIgnoreCase(df, sourceKeyName);
            if (keyCol == null)
            {
                throw new Exception($"Join key column '{sourceKeyName}' not found in left frame.");
            }

            var newCols = new List<ISeries>(df.Columns.Count + 1);
            newCols.AddRange(df.Columns);
            newCols.Add(CloneSeriesWithName(keyCol, targetKeyName));
            return new DataFrame(newCols);
        }

        private static DataFrame PrepareJoinedRightFrame(
            DataFrame df,
            Dictionary<string, string> targetNames,
            string rightKeyOrig,
            string joinKeyName)
        {
            ISeries? rightKeyCol = FindColumnIgnoreCase(df, rightKeyOrig);
            if (rightKeyCol == null)
            {
                throw new Exception($"Join key column '{rightKeyOrig}' not found in right frame.");
            }

            var newCols = new List<ISeries>(df.Columns.Count + 1);
            foreach (var col in df.Columns)
            {
                if (targetNames.TryGetValue(col.Name, out string? targetName) &&
                    !col.Name.Equals(targetName, StringComparison.Ordinal))
                {
                    newCols.Add(CloneSeriesWithName(col, targetName));
                }
                else
                {
                    newCols.Add(col);
                }
            }

            newCols.Add(CloneSeriesWithName(rightKeyCol, joinKeyName));
            return new DataFrame(newCols);
        }

        private static ISeries? FindColumnIgnoreCase(DataFrame df, string colName)
        {
            foreach (var col in df.Columns)
            {
                if (col.Name.Equals(colName, StringComparison.Ordinal))
                {
                    return col;
                }
            }
            foreach (var col in df.Columns)
            {
                if (col.Name.Equals(colName, StringComparison.OrdinalIgnoreCase))
                {
                    return col;
                }
            }
            return null;
        }

        internal static DataFrame CloneDataFrameShallow(DataFrame source)
        {
            var clonedCols = new List<ISeries>(source.Columns.Count);
            foreach (var col in source.Columns)
            {
                clonedCols.Add(CloneSeriesWithName(col, col.Name));
            }
            return new DataFrame(clonedCols);
        }

        internal static ISeries CloneSeriesWithName(ISeries source, string newName)
        {
            int len = source.Length;
            if (len == 0)
            {
                if (source is Utf8StringSeries)
                {
                    return new Utf8StringSeries(newName, 0, 0);
                }
                var empty = source.CloneEmpty(0);
                empty.Rename(newName);
                return empty;
            }

            if (source is Int32Series i32)
            {
                var dst = new Int32Series(newName, len);
                i32.Memory.Span.Slice(0, len).CopyTo(dst.Memory.Span);
                if (i32.ValidityMask.HasNulls) i32.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }
            if (source is Int64Series i64)
            {
                var dst = new Int64Series(newName, len);
                i64.Memory.Span.Slice(0, len).CopyTo(dst.Memory.Span);
                if (i64.ValidityMask.HasNulls) i64.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }
            if (source is Float64Series f64)
            {
                var dst = new Float64Series(newName, len);
                f64.Memory.Span.Slice(0, len).CopyTo(dst.Memory.Span);
                if (f64.ValidityMask.HasNulls) f64.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }
            if (source is BooleanSeries bs)
            {
                var dst = new BooleanSeries(newName, len);
                bs.Memory.Span.Slice(0, len).CopyTo(dst.Memory.Span);
                if (bs.ValidityMask.HasNulls) bs.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }
            if (source is TimeSeries ts)
            {
                var dst = new TimeSeries(newName, len);
                ts.Memory.Span.Slice(0, len).CopyTo(dst.Memory.Span);
                if (ts.ValidityMask.HasNulls) ts.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }
            if (source is Utf8StringSeries u8)
            {
                int startOffset = u8.Offsets.Span[0];
                int endOffset = u8.Offsets.Span[len];
                int byteLen = endOffset - startOffset;
                var dst = new Utf8StringSeries(newName, len, byteLen);
                if (byteLen > 0)
                {
                    u8.DataBytes.Span.Slice(startOffset, byteLen).CopyTo(dst.DataBytes.Span);
                }
                if (startOffset == 0)
                {
                    u8.Offsets.Span.Slice(0, len + 1).CopyTo(dst.Offsets.Span);
                }
                else
                {
                    var srcOffsets = u8.Offsets.Span;
                    var dstOffsets = dst.Offsets.Span;
                    for (int i = 0; i <= len; i++)
                    {
                        dstOffsets[i] = srcOffsets[i] - startOffset;
                    }
                }
                if (u8.ValidityMask.HasNulls) u8.ValidityMask.CopyTo(dst.ValidityMask, 0);
                return dst;
            }

            var indices = new int[len];
            for (int i = 0; i < len; i++) indices[i] = i;
            var fallback = source.CloneEmpty(len);
            fallback.Rename(newName);
            source.Take(fallback, indices);
            return fallback;
        }
    }
}
