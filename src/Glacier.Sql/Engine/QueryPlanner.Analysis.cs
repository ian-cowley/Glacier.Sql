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
        private sealed class SubqueryCounter
        {
            private int _nextId;
            public int Next() => _nextId++;
        }

        private readonly struct CorrelationKey : IEquatable<CorrelationKey>
        {
            private readonly object?[] _values;
            private readonly int _hash;

            public CorrelationKey(object?[] values)
            {
                _values = values;
                var hc = new HashCode();
                for (int i = 0; i < values.Length; i++)
                {
                    hc.Add(values[i]);
                }
                _hash = hc.ToHashCode();
            }

            public bool Equals(CorrelationKey other)
            {
                if (_values.Length != other._values.Length || _hash != other._hash) return false;
                for (int i = 0; i < _values.Length; i++)
                {
                    if (!Equals(_values[i], other._values[i])) return false;
                }
                return true;
            }

            public override bool Equals(object? obj) => obj is CorrelationKey other && Equals(other);
            public override int GetHashCode() => _hash;
        }

        private sealed class MaterializedInSubqueryData
        {
            public List<object> Values { get; }
            public bool HasNull { get; }

            public MaterializedInSubqueryData(List<object> values, bool hasNull)
            {
                Values = values;
                HasNull = hasNull;
            }
        }

        private sealed class ActiveTableInfo
        {
            public string Name { get; }
            public string? Alias { get; }
            public TableMetadata Metadata { get; }
            public Dictionary<string, string> PhysicalColumns { get; }

            public ActiveTableInfo(string name, string? alias, TableMetadata metadata, Dictionary<string, string> physicalColumns)
            {
                Name = name;
                Alias = alias;
                Metadata = metadata;
                PhysicalColumns = physicalColumns;
            }
        }

        private static ActiveTableInfo CreateIdentityActiveTable(string name, string? alias, TableMetadata metadata)
        {
            var map = new Dictionary<string, string>(metadata.Columns.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var col in metadata.Columns)
            {
                map[col.Name] = col.Name;
            }
            return new ActiveTableInfo(name, alias, metadata, map);
        }

        private string ResolveColumnName(SqlColumnRef colRef, List<ActiveTableInfo> activeTables)
        {
            if (colRef.Prefix == null && colRef.ColumnName.StartsWith("__subq_", StringComparison.Ordinal))
            {
                return colRef.ColumnName;
            }

            // If column has prefix (table name or alias)
            if (colRef.Prefix != null)
            {
                var matchedTable = activeTables.FirstOrDefault(t =>
                    (t.Alias ?? t.Name).Equals(colRef.Prefix, StringComparison.OrdinalIgnoreCase));

                if (matchedTable == null)
                {
                    throw new Exception($"Unknown table alias/prefix '{colRef.Prefix}' for column '{colRef.ColumnName}'.");
                }

                if (!matchedTable.PhysicalColumns.TryGetValue(colRef.ColumnName, out string? physicalName))
                {
                    throw new Exception($"Column '{colRef.ColumnName}' does not exist in table '{colRef.Prefix}'.");
                }

                return physicalName;
            }

            // Unprefixed column reference. Search in all tables in FROM/JOIN order.
            foreach (var t in activeTables)
            {
                if (t.PhysicalColumns.TryGetValue(colRef.ColumnName, out string? physicalName))
                {
                    return physicalName;
                }
            }

            throw new Exception($"Column '{colRef.ColumnName}' does not exist in any active table schemas.");
        }

        private bool TryResolveColumnName(
            SqlColumnRef colRef,
            List<ActiveTableInfo> tables,
            out string resolvedName)
        {
            if (colRef.Prefix != null)
            {
                var matchedTable = tables.FirstOrDefault(t =>
                    (t.Alias ?? t.Name).Equals(colRef.Prefix, StringComparison.OrdinalIgnoreCase));

                if (matchedTable == null ||
                    !matchedTable.PhysicalColumns.TryGetValue(colRef.ColumnName, out string? phys))
                {
                    resolvedName = string.Empty;
                    return false;
                }

                resolvedName = phys;
                return true;
            }

            foreach (var t in tables)
            {
                if (t.PhysicalColumns.TryGetValue(colRef.ColumnName, out string? phys))
                {
                    resolvedName = phys;
                    return true;
                }
            }

            resolvedName = string.Empty;
            return false;
        }

        private (string LeftKeyName, string RightKeyName) ResolveJoinKeysBidirectional(
            SqlColumnRef leftCol,
            SqlColumnRef rightCol,
            List<ActiveTableInfo> leftTables,
            List<ActiveTableInfo> rightTables)
        {
            if (TryResolveColumnName(leftCol, leftTables, out string leftNormal) &&
                TryResolveColumnName(rightCol, rightTables, out string rightNormal))
            {
                return (leftNormal, rightNormal);
            }

            if (TryResolveColumnName(rightCol, leftTables, out string leftSwapped) &&
                TryResolveColumnName(leftCol, rightTables, out string rightSwapped))
            {
                return (leftSwapped, rightSwapped);
            }

            // Fall back to ResolveColumnName so descriptive error is thrown
            return (ResolveColumnName(leftCol, leftTables), ResolveColumnName(rightCol, rightTables));
        }

        private async Task<(SqlExpression RewrittenExpr, LazyFrame? UpdatedLazy)> MaterializeSubqueriesInExpressionAsync(
            SqlExpression expr,
            List<ActiveTableInfo>? activeTables,
            LazyFrame? currentLazy,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?> materializedSubqueries,
            SubqueryCounter subqueryCounter)
        {
            switch (expr)
            {
                case SqlExistsExpression existsExpr:
                {
                    var subQuery = CloneSelectStatement(existsExpr.SelectQuery);
                    var innerActiveTables = GetActiveTablesForSelect(subQuery);

                    if (subQuery.Where != null && activeTables != null &&
                        TryRewriteCorrelatedOrExists(subQuery, activeTables, innerActiveTables, out var expandedOrExists) &&
                        expandedOrExists != null)
                    {
                        return await MaterializeSubqueriesInExpressionAsync(
                            expandedOrExists,
                            activeTables,
                            currentLazy,
                            inserted,
                            deleted,
                            materializedSubqueries,
                            subqueryCounter).ConfigureAwait(false);
                    }

                    if (subQuery.Where != null && activeTables != null &&
                        CanHoistOuterOnlyWhereInExists(subQuery) &&
                        TrySplitOuterOnlyExistsConjuncts(subQuery, activeTables, innerActiveTables, subQuery.Where, out var hoistedExistsExpr) &&
                        hoistedExistsExpr != null)
                    {
                        return await MaterializeSubqueriesInExpressionAsync(
                            hoistedExistsExpr,
                            activeTables,
                            currentLazy,
                            inserted,
                            deleted,
                            materializedSubqueries,
                            subqueryCounter).ConfigureAwait(false);
                    }

                    var correlations = new List<(SqlColumnRef innerCol, SqlColumnRef outerCol)>();
                    SqlExpression? remainingWhere = null;

                    if (subQuery.Where != null && activeTables != null)
                    {
                        ExtractCorrelations(subQuery.Where, activeTables, innerActiveTables, correlations, out remainingWhere);
                    }
                    else
                    {
                        remainingWhere = subQuery.Where;
                    }

                    subQuery.Where = remainingWhere;

                    if (correlations.Count == 0)
                    {
                        var subLazy = await PlanQueryAsync(subQuery, inserted, deleted).ConfigureAwait(false);
                        var subDf = await subLazy.Limit(1).Collect().ConfigureAwait(false);
                        return (new SqlLiteral(subDf.RowCount > 0), currentLazy);
                    }

                    // Correlated EXISTS with 1 or more correlation keys (works for any column type and any boolean nesting: AND, OR, NOT)
                    if (currentLazy == null || activeTables == null)
                    {
                        throw new InvalidOperationException("Correlated EXISTS requires an active outer FROM clause.");
                    }

                    bool hasComplexClauses = subQuery.Top.HasValue ||
                                             subQuery.GroupBy.Count > 0 ||
                                             subQuery.Projections.Any(p => HasAggregateFunction(p.Expression));

                    var innerColNames = new string[correlations.Count];
                    if (!hasComplexClauses && subQuery.Joins.Count == 0)
                    {
                        // Project bare correlation columns without renaming so Polaris SelectBatch never mutates source table ISeries.Name in-place
                        subQuery.Projections.Clear();
                        var addedInnerCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        for (int k = 0; k < correlations.Count; k++)
                        {
                            string resolvedInner = ResolveColumnName(correlations[k].innerCol, innerActiveTables);
                            innerColNames[k] = resolvedInner;
                            if (addedInnerCols.Add(resolvedInner))
                            {
                                subQuery.Projections.Add(new SelectItem(new SqlColumnRef(resolvedInner), alias: null));
                            }
                        }
                    }
                    else
                    {
                        if (!hasComplexClauses)
                        {
                            subQuery.Projections.Clear();
                        }
                        for (int k = 0; k < correlations.Count; k++)
                        {
                            string alias = $"__corr_{k}";
                            innerColNames[k] = alias;
                            subQuery.Projections.Add(new SelectItem(correlations[k].innerCol, alias));
                        }
                    }

                    var corrSubLazy = await PlanQueryAsync(subQuery, inserted, deleted).ConfigureAwait(false);
                    var corrSubDf = await corrSubLazy.Collect().ConfigureAwait(false);

                    var innerKeySeries = new ISeries[correlations.Count];
                    for (int k = 0; k < correlations.Count; k++)
                    {
                        innerKeySeries[k] = corrSubDf.GetColumn(innerColNames[k]);
                    }

                    var keySet = new HashSet<CorrelationKey>();
                    for (int r = 0; r < corrSubDf.RowCount; r++)
                    {
                        var vals = new object?[correlations.Count];
                        bool anyNull = false;
                        for (int k = 0; k < correlations.Count; k++)
                        {
                            if (!innerKeySeries[k].ValidityMask.IsValid(r))
                            {
                                anyNull = true;
                                break;
                            }
                            vals[k] = innerKeySeries[k].Get(r);
                            if (vals[k] == null)
                            {
                                anyNull = true;
                                break;
                            }
                        }
                        if (!anyNull)
                        {
                            keySet.Add(new CorrelationKey(vals));
                        }
                    }

                    var outerDf = await currentLazy.Collect().ConfigureAwait(false);
                    var outerKeySeries = new ISeries[correlations.Count];
                    for (int k = 0; k < correlations.Count; k++)
                    {
                        string outerColName = ResolveColumnName(correlations[k].outerCol, activeTables);
                        outerKeySeries[k] = outerDf.GetColumn(outerColName);
                    }

                    string markerColName = $"__subq_{subqueryCounter.Next()}";
                    var matchSeries = new BooleanSeries(markerColName, outerDf.RowCount);
                    var matchSpan = matchSeries.Memory.Span;

                    if (keySet.Count > 0)
                    {
                        var probeVals = new object?[correlations.Count];
                        for (int r = 0; r < outerDf.RowCount; r++)
                        {
                            bool anyNull = false;
                            for (int k = 0; k < correlations.Count; k++)
                            {
                                if (!outerKeySeries[k].ValidityMask.IsValid(r))
                                {
                                    anyNull = true;
                                    break;
                                }
                                probeVals[k] = outerKeySeries[k].Get(r);
                                if (probeVals[k] == null)
                                {
                                    anyNull = true;
                                    break;
                                }
                            }
                            matchSpan[r] = !anyNull && keySet.Contains(new CorrelationKey(probeVals));
                        }
                    }

                    var augmentedCols = new List<ISeries>(outerDf.Columns.Count + 1);
                    augmentedCols.AddRange(outerDf.Columns);
                    augmentedCols.Add(matchSeries);
                    var augmentedDf = new DataFrame(augmentedCols);

                    return (new SqlColumnRef(markerColName), augmentedDf.Lazy());
                }

                case SqlSubqueryExpression subqueryExpr:
                {
                    var subLazy = await PlanQueryAsync(CloneSelectStatement(subqueryExpr.SelectQuery), inserted, deleted).ConfigureAwait(false);
                    var subDf = await subLazy.Limit(1).Collect().ConfigureAwait(false);
                    object? val = null;
                    if (subDf.RowCount > 0 && subDf.Columns.Count > 0)
                    {
                        var firstCol = subDf.Columns[0];
                        if (firstCol.ValidityMask.IsValid(0))
                        {
                            val = firstCol.Get(0);
                        }
                    }
                    return (new SqlLiteral(val), currentLazy);
                }

                case SqlInSubqueryExpression inSubqueryExpr:
                {
                    var (rewrittenLeft, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                        inSubqueryExpr.Left, activeTables, currentLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);

                    var subLazy = await PlanQueryAsync(CloneSelectStatement(inSubqueryExpr.Subquery), inserted, deleted).ConfigureAwait(false);
                    var subDf = await subLazy.Collect().ConfigureAwait(false);

                    var values = new List<object>();
                    bool hasNull = false;
                    if (subDf.RowCount > 0 && subDf.Columns.Count > 0)
                    {
                        var subCol = subDf.Columns[0];
                        var mask = subCol.ValidityMask;
                        for (int i = 0; i < subCol.Length; i++)
                        {
                            if (!mask.IsValid(i))
                            {
                                hasNull = true;
                                continue;
                            }
                            var v = subCol.Get(i);
                            if (v == null)
                            {
                                hasNull = true;
                            }
                            else
                            {
                                values.Add(v);
                            }
                        }
                    }

                    var node = ReferenceEquals(rewrittenLeft, inSubqueryExpr.Left)
                        ? inSubqueryExpr
                        : new SqlInSubqueryExpression(rewrittenLeft, inSubqueryExpr.Subquery);
                    materializedSubqueries[node] = new MaterializedInSubqueryData(values, hasNull);
                    return (node, updatedLazy);
                }

                case SqlInListExpression inListExpr:
                {
                    var (rewrittenLeft, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                        inListExpr.Left, activeTables, currentLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);

                    var rewrittenItems = new List<SqlExpression>(inListExpr.List.Count);
                    bool changed = !ReferenceEquals(rewrittenLeft, inListExpr.Left);
                    foreach (var item in inListExpr.List)
                    {
                        var (rewrittenItem, nextLazy) = await MaterializeSubqueriesInExpressionAsync(
                            item, activeTables, updatedLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);
                        updatedLazy = nextLazy;
                        if (!ReferenceEquals(rewrittenItem, item)) changed = true;
                        rewrittenItems.Add(rewrittenItem);
                    }

                    return (changed ? new SqlInListExpression(rewrittenLeft, rewrittenItems) : inListExpr, updatedLazy);
                }

                case SqlBinaryExpression binary:
                {
                    var (rewrittenLeft, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                        binary.Left, activeTables, currentLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);
                    var (rewrittenRight, finalLazy) = await MaterializeSubqueriesInExpressionAsync(
                        binary.Right, activeTables, updatedLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);

                    if (ReferenceEquals(rewrittenLeft, binary.Left) && ReferenceEquals(rewrittenRight, binary.Right))
                    {
                        return (binary, finalLazy);
                    }
                    return (new SqlBinaryExpression(rewrittenLeft, binary.Operator, rewrittenRight), finalLazy);
                }

                case SqlUnaryExpression unary:
                {
                    var (rewrittenOperand, updatedLazy) = await MaterializeSubqueriesInExpressionAsync(
                        unary.Operand, activeTables, currentLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);
                    if (ReferenceEquals(rewrittenOperand, unary.Operand))
                    {
                        return (unary, updatedLazy);
                    }
                    return (new SqlUnaryExpression(unary.Operator, rewrittenOperand), updatedLazy);
                }

                case SqlFunctionCall fn:
                {
                    var updatedLazy = currentLazy;
                    var newArgs = new List<SqlExpression>(fn.Arguments.Count);
                    bool changed = false;
                    foreach (var arg in fn.Arguments)
                    {
                        var (rewrittenArg, nextLazy) = await MaterializeSubqueriesInExpressionAsync(
                            arg, activeTables, updatedLazy, inserted, deleted, materializedSubqueries, subqueryCounter).ConfigureAwait(false);
                        updatedLazy = nextLazy;
                        if (!ReferenceEquals(rewrittenArg, arg)) changed = true;
                        newArgs.Add(rewrittenArg);
                    }
                    return (changed ? new SqlFunctionCall(fn.FunctionName, newArgs) : fn, updatedLazy);
                }

                default:
                    return (expr, currentLazy);
            }
        }

        private string GetSqlDataType(ISeries col)
        {
            var type = col.DataType;
            string className = col.GetType().Name;
            if (className.Contains("Int32") || type == typeof(int)) return "INT";
            if (className.Contains("Float64") || className.Contains("Double") || type == typeof(double)) return "FLOAT";
            if (className.Contains("Boolean") || className.Contains("Bool") || type == typeof(bool)) return "BIT";
            if (className.Contains("Time") || className.Contains("Date") || className.Contains("DateTime")) return "DATETIME";
            if (className.Contains("Int64") || type == typeof(long)) return "INT";
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

        private List<ActiveTableInfo> GetActiveTablesForSelect(SelectStatement select)
        {
            var active = new List<ActiveTableInfo>();
            if (select.From is SqlTableSource source)
            {
                TableMetadata? meta = ResolveMetadataForCorrelation(source.TableName);
                if (meta != null)
                {
                    active.Add(CreateIdentityActiveTable(source.TableName, source.Alias, meta));
                }
            }

            foreach (var join in select.Joins)
            {
                if (join.Table is SqlTableSource joinSource)
                {
                    TableMetadata? meta = ResolveMetadataForCorrelation(joinSource.TableName);
                    if (meta != null)
                    {
                        active.Add(CreateIdentityActiveTable(joinSource.TableName, joinSource.Alias, meta));
                    }
                }
            }

            return active;
        }

        private TableMetadata? ResolveMetadataForCorrelation(string tableName)
        {
            if (tableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
            {
                return new TableMetadata { TableName = "inserted", Columns = new() };
            }
            if (tableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                return new TableMetadata { TableName = "deleted", Columns = new() };
            }
            if (_catalog.ViewExists(tableName))
            {
                return new TableMetadata { TableName = tableName, Columns = new() };
            }
            return _catalog.GetTable(tableName);
        }

        private bool CanHoistOuterOnlyWhereInExists(SelectStatement subQuery)
        {
            return subQuery.GroupBy.Count == 0 &&
                   subQuery.Having == null &&
                   !subQuery.Projections.Any(p => HasAggregateFunction(p.Expression)) &&
                   (!subQuery.Top.HasValue || subQuery.Top.Value > 0);
        }

        private bool TryRewriteCorrelatedOrExists(
            SelectStatement subQuery,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables,
            out SqlExpression? rewritten)
        {
            rewritten = null;
            if (subQuery.Where == null || !CanHoistOuterOnlyWhereInExists(subQuery))
            {
                return false;
            }

            if (!ContainsCorrelatedOr(subQuery.Where, outerTables, innerTables))
            {
                return false;
            }

            var branches = ExpandCorrelatedOr(subQuery.Where, outerTables, innerTables, maxBranches: 16);
            if (branches == null || branches.Count <= 1)
            {
                return false;
            }

            SqlExpression? combined = null;
            foreach (var branchWhere in branches)
            {
                SqlExpression branchExpr;
                if (TrySplitOuterOnlyExistsConjuncts(subQuery, outerTables, innerTables, branchWhere, out var splitExpr) &&
                    splitExpr != null)
                {
                    branchExpr = splitExpr;
                }
                else
                {
                    var branchSelect = CloneSelectStatement(subQuery);
                    branchSelect.Where = branchWhere;
                    branchExpr = new SqlExistsExpression(branchSelect);
                }

                combined = combined == null ? branchExpr : new SqlBinaryExpression(combined, "OR", branchExpr);
            }

            rewritten = combined;
            return rewritten != null;
        }

        private bool TrySplitOuterOnlyExistsConjuncts(
            SelectStatement subQuery,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables,
            SqlExpression whereExpr,
            out SqlExpression? rewritten)
        {
            rewritten = null;
            var conjuncts = new List<SqlExpression>();
            FlattenAndConjuncts(whereExpr, conjuncts);

            var innerOrMixed = new List<SqlExpression>(conjuncts.Count);
            var outerOnly = new List<SqlExpression>();

            foreach (var conjunct in conjuncts)
            {
                if (ReferencesOuterTable(conjunct, outerTables, innerTables) &&
                    !ReferencesInnerTable(conjunct, innerTables))
                {
                    outerOnly.Add(conjunct);
                }
                else
                {
                    innerOrMixed.Add(conjunct);
                }
            }

            if (outerOnly.Count == 0)
            {
                return false;
            }

            var innerSelect = CloneSelectStatement(subQuery);
            innerSelect.Where = CombineWithAnd(innerOrMixed);
            var innerExists = new SqlExistsExpression(innerSelect);

            var outerPred = CombineWithAnd(outerOnly)!;
            // EXISTS never returns SQL UNKNOWN: coerce outer NULL evaluations to FALSE so NOT EXISTS works accurately.
            var safeOuterPred = new SqlBinaryExpression(
                new SqlBinaryExpression(outerPred, "IS NOT", new SqlLiteral(null)),
                "AND",
                outerPred);

            rewritten = new SqlBinaryExpression(innerExists, "AND", safeOuterPred);
            return true;
        }

        private static void FlattenAndConjuncts(SqlExpression expr, List<SqlExpression> result)
        {
            if (expr is SqlBinaryExpression bin && bin.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                FlattenAndConjuncts(bin.Left, result);
                FlattenAndConjuncts(bin.Right, result);
                return;
            }
            result.Add(expr);
        }

        private static SqlExpression? CombineWithAnd(List<SqlExpression> conjuncts)
        {
            SqlExpression? combined = null;
            foreach (var c in conjuncts)
            {
                combined = combined == null ? c : new SqlBinaryExpression(combined, "AND", c);
            }
            return combined;
        }

        private bool ReferencesInnerTable(
            SqlExpression expr,
            List<ActiveTableInfo> innerTables)
        {
            switch (expr)
            {
                case SqlColumnRef colRef:
                    return colRef.Prefix != null
                        ? IsTableInList(colRef.Prefix, innerTables)
                        : TryResolveColumnName(colRef, innerTables, out _);
                case SqlBinaryExpression bin:
                    return ReferencesInnerTable(bin.Left, innerTables) ||
                           ReferencesInnerTable(bin.Right, innerTables);
                case SqlUnaryExpression un:
                    return ReferencesInnerTable(un.Operand, innerTables);
                case SqlFunctionCall fn:
                    return fn.Arguments.Any(a => ReferencesInnerTable(a, innerTables));
                case SqlInListExpression inList:
                    return ReferencesInnerTable(inList.Left, innerTables) ||
                           inList.List.Any(i => ReferencesInnerTable(i, innerTables));
                case SqlExistsExpression:
                case SqlSubqueryExpression:
                case SqlInSubqueryExpression:
                    return true;
                default:
                    return false;
            }
        }

        private static bool ReferencesOuterTable(
            SqlExpression expr,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables)
        {
            switch (expr)
            {
                case SqlColumnRef colRef:
                    return colRef.Prefix != null &&
                           IsTableInList(colRef.Prefix, outerTables) &&
                           !IsTableInList(colRef.Prefix, innerTables);
                case SqlBinaryExpression bin:
                    return ReferencesOuterTable(bin.Left, outerTables, innerTables) ||
                           ReferencesOuterTable(bin.Right, outerTables, innerTables);
                case SqlUnaryExpression un:
                    return ReferencesOuterTable(un.Operand, outerTables, innerTables);
                case SqlFunctionCall fn:
                    return fn.Arguments.Any(a => ReferencesOuterTable(a, outerTables, innerTables));
                case SqlInListExpression inList:
                    return ReferencesOuterTable(inList.Left, outerTables, innerTables) ||
                           inList.List.Any(i => ReferencesOuterTable(i, outerTables, innerTables));
                default:
                    return false;
            }
        }

        private static bool ContainsCorrelatedOr(
            SqlExpression expr,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables)
        {
            if (expr is SqlBinaryExpression bin)
            {
                if (bin.Operator.Equals("OR", StringComparison.OrdinalIgnoreCase))
                {
                    return ReferencesOuterTable(bin.Left, outerTables, innerTables) ||
                           ReferencesOuterTable(bin.Right, outerTables, innerTables);
                }

                if (bin.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    return ContainsCorrelatedOr(bin.Left, outerTables, innerTables) ||
                           ContainsCorrelatedOr(bin.Right, outerTables, innerTables);
                }
            }

            return false;
        }

        private static List<SqlExpression>? ExpandCorrelatedOr(
            SqlExpression expr,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables,
            int maxBranches)
        {
            if (expr is SqlBinaryExpression bin)
            {
                if (bin.Operator.Equals("OR", StringComparison.OrdinalIgnoreCase) &&
                    (ReferencesOuterTable(bin.Left, outerTables, innerTables) ||
                     ReferencesOuterTable(bin.Right, outerTables, innerTables)))
                {
                    var leftBranches = ExpandCorrelatedOr(bin.Left, outerTables, innerTables, maxBranches);
                    if (leftBranches == null) return null;
                    var rightBranches = ExpandCorrelatedOr(bin.Right, outerTables, innerTables, maxBranches);
                    if (rightBranches == null) return null;
                    if (leftBranches.Count + rightBranches.Count > maxBranches) return null;

                    var merged = new List<SqlExpression>(leftBranches.Count + rightBranches.Count);
                    merged.AddRange(leftBranches);
                    merged.AddRange(rightBranches);
                    return merged;
                }

                if (bin.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    var leftBranches = ContainsCorrelatedOr(bin.Left, outerTables, innerTables)
                        ? ExpandCorrelatedOr(bin.Left, outerTables, innerTables, maxBranches)
                        : new List<SqlExpression> { bin.Left };
                    if (leftBranches == null) return null;

                    var rightBranches = ContainsCorrelatedOr(bin.Right, outerTables, innerTables)
                        ? ExpandCorrelatedOr(bin.Right, outerTables, innerTables, maxBranches)
                        : new List<SqlExpression> { bin.Right };
                    if (rightBranches == null) return null;

                    if ((long)leftBranches.Count * rightBranches.Count > maxBranches) return null;

                    var distributed = new List<SqlExpression>(leftBranches.Count * rightBranches.Count);
                    foreach (var l in leftBranches)
                    {
                        foreach (var r in rightBranches)
                        {
                            distributed.Add(new SqlBinaryExpression(l, "AND", r));
                        }
                    }
                    return distributed;
                }
            }

            return new List<SqlExpression> { expr };
        }

        private void ExtractCorrelations(
            SqlExpression expr,
            List<ActiveTableInfo> outerTables,
            List<ActiveTableInfo> innerTables,
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

        private static bool IsTableInList(string? prefix, List<ActiveTableInfo> tables)
        {
            if (prefix == null) return false;
            return tables.Any(t => (t.Alias ?? t.Name).Equals(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
