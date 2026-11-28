using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;

namespace Glacier.Sql.Engine
{
    public partial class QueryPlanner
    {
        public Expr CompileExpression(SqlExpression expr, string tableName)
        {
            var tableMeta = _catalog.GetTable(tableName) ?? throw new Exception($"Table '{tableName}' does not exist.");
            var activeTables = new List<ActiveTableInfo>
            {
                CreateIdentityActiveTable(tableName, null, tableMeta)
            };
            return CompileExpression(expr, activeTables);
        }

        private static bool IsAggregateFunction(SqlFunctionCall call)
        {
            string fn = call.FunctionName.ToUpperInvariant();
            return fn == "SUM" || fn == "AVG" || fn == "MIN" || fn == "MAX" || fn == "COUNT";
        }

        private static bool IsBitColumn(SqlExpression expr, List<ActiveTableInfo> activeTables)
        {
            if (expr is not SqlColumnRef colRef) return false;
            foreach (var t in activeTables)
            {
                if (colRef.Prefix != null &&
                    !colRef.Prefix.Equals(t.Alias ?? t.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colMeta = t.Metadata.Columns.FirstOrDefault(c => c.Name.Equals(colRef.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (colMeta != null)
                {
                    string dt = colMeta.DataType.ToUpperInvariant();
                    return dt == "BIT" || dt == "BOOLEAN";
                }
            }
            return false;
        }

        private delegate Expr CompileWithNullDelegate(SqlExpression expr, out bool isAlwaysNull);

        private static Expr CreateNullColumnExpr()
        {
            // Produces an Int64Series of df.RowCount with all rows set to NULL without calling Expr.Lit(null!).
            return Expr.Lit(string.Empty).Cast(typeof(long));
        }

        private static Expr CreateUnknownBooleanExpr()
        {
            // Produces a BooleanSeries of df.RowCount whose ValidityMask is all NULL (SQL UNKNOWN).
            return Expr.Lit(string.Empty).Cast(typeof(double)) == Expr.Lit(0.0);
        }

        private bool HasAggregateFunction(SqlExpression expr)
        {
            return expr switch
            {
                SqlFunctionCall call => IsAggregateFunction(call) || call.Arguments.Any(HasAggregateFunction),
                SqlBinaryExpression binary => HasAggregateFunction(binary.Left) || HasAggregateFunction(binary.Right),
                SqlUnaryExpression unary => HasAggregateFunction(unary.Operand),
                SqlInListExpression inList => HasAggregateFunction(inList.Left) || inList.List.Any(HasAggregateFunction),
                _ => false
            };
        }

        private void ExtractAggregateCalls(
            SqlExpression expr,
            List<(string Key, SqlFunctionCall Call, string TempAlias)> aggList,
            Dictionary<string, string> keyToTempAlias,
            List<ActiveTableInfo> activeTables)
        {
            switch (expr)
            {
                case SqlFunctionCall call when IsAggregateFunction(call):
                    string key = GetAggregateCanonicalKey(call, activeTables);
                    if (!keyToTempAlias.ContainsKey(key))
                    {
                        string tempAlias = $"__agg_{aggList.Count}";
                        keyToTempAlias[key] = tempAlias;
                        aggList.Add((key, call, tempAlias));
                    }
                    break;

                case SqlFunctionCall call:
                    foreach (var arg in call.Arguments)
                    {
                        ExtractAggregateCalls(arg, aggList, keyToTempAlias, activeTables);
                    }
                    break;

                case SqlBinaryExpression binary:
                    ExtractAggregateCalls(binary.Left, aggList, keyToTempAlias, activeTables);
                    ExtractAggregateCalls(binary.Right, aggList, keyToTempAlias, activeTables);
                    break;

                case SqlUnaryExpression unary:
                    ExtractAggregateCalls(unary.Operand, aggList, keyToTempAlias, activeTables);
                    break;

                case SqlInListExpression inList:
                    ExtractAggregateCalls(inList.Left, aggList, keyToTempAlias, activeTables);
                    foreach (var item in inList.List)
                    {
                        ExtractAggregateCalls(item, aggList, keyToTempAlias, activeTables);
                    }
                    break;
            }
        }

        private string GetAggregateCanonicalKey(
            SqlFunctionCall call,
            List<ActiveTableInfo> activeTables)
        {
            var argsKey = string.Join(",", call.Arguments.Select(a => FormatCanonicalExpr(a, activeTables)));
            return $"{call.FunctionName.ToUpperInvariant()}({argsKey})";
        }

        private string FormatCanonicalExpr(
            SqlExpression expr,
            List<ActiveTableInfo> activeTables)
        {
            return expr switch
            {
                SqlStarRef => "*",
                SqlColumnRef col => $"COL:{ResolveColumnName(col, activeTables).ToUpperInvariant()}",
                SqlLiteral lit => lit.Value == null ? "NULL" : $"LIT:{lit.Value.GetType().Name}:{Convert.ToString(lit.Value, CultureInfo.InvariantCulture)}",
                SqlBinaryExpression bin => $"({FormatCanonicalExpr(bin.Left, activeTables)} {bin.Operator.ToUpperInvariant()} {FormatCanonicalExpr(bin.Right, activeTables)})",
                SqlUnaryExpression un => $"({un.Operator.ToUpperInvariant()} {FormatCanonicalExpr(un.Operand, activeTables)})",
                SqlFunctionCall fn => $"{fn.FunctionName.ToUpperInvariant()}({string.Join(",", fn.Arguments.Select(a => FormatCanonicalExpr(a, activeTables)))})",
                _ => expr.ToString() ?? string.Empty
            };
        }

        private Expr CompilePostAggregateExpression(
            SqlExpression sqlExpr,
            List<ActiveTableInfo> activeTables,
            Dictionary<string, string> keyToTempAlias,
            Dictionary<string, Expr>? aliasExprMap,
            HashSet<string> groupColumnsSet,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries = null)
        {
            return CompilePostAggregateExpressionCore(
                sqlExpr,
                activeTables,
                keyToTempAlias,
                aliasExprMap,
                groupColumnsSet,
                inserted,
                deleted,
                materializedSubqueries,
                out _);
        }

        private Expr CompilePostAggregateExpressionCore(
            SqlExpression sqlExpr,
            List<ActiveTableInfo> activeTables,
            Dictionary<string, string> keyToTempAlias,
            Dictionary<string, Expr>? aliasExprMap,
            HashSet<string> groupColumnsSet,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries,
            out bool isAlwaysNull)
        {
            Expr CompileSub(SqlExpression e, out bool subNull) =>
                CompilePostAggregateExpressionCore(e, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted, materializedSubqueries, out subNull);

            switch (sqlExpr)
            {
                case SqlLiteral lit:
                    if (lit.Value == null)
                    {
                        isAlwaysNull = true;
                        return CreateNullColumnExpr();
                    }
                    isAlwaysNull = false;
                    return Expr.Lit(lit.Value);

                case SqlColumnRef col:
                    isAlwaysNull = false;
                    if (col.Prefix == null && aliasExprMap != null && aliasExprMap.TryGetValue(col.ColumnName, out var aliasedExpr))
                    {
                        return aliasedExpr;
                    }
                    string resolvedName = ResolveColumnName(col, activeTables);
                    if (!groupColumnsSet.Contains(resolvedName))
                    {
                        throw new Exception($"Column '{col}' must appear in the GROUP BY clause or be used in an aggregate function.");
                    }
                    return Expr.Col(resolvedName);

                case SqlFunctionCall call when IsAggregateFunction(call):
                    isAlwaysNull = false;
                    string key = GetAggregateCanonicalKey(call, activeTables);
                    if (!keyToTempAlias.TryGetValue(key, out var tempAlias))
                    {
                        throw new Exception($"Internal planner error: aggregate '{call}' was not extracted.");
                    }
                    return Expr.Col(tempAlias);

                case SqlFunctionCall call:
                    return CompileNonAggregateFunctionWithNull(call, CompileSub, out isAlwaysNull);

                case SqlUnaryExpression unary:
                    return CompileUnaryWithNull(unary, CompileSub, activeTables, inserted, deleted, materializedSubqueries, out isAlwaysNull);

                case SqlBinaryExpression binary:
                    return CompileBinaryWithNull(binary, CompileSub, activeTables, out isAlwaysNull);

                case SqlInListExpression inListExpr:
                    isAlwaysNull = false;
                    return CompileInList(inListExpr, negated: false, CompileSub);

                case SqlInSubqueryExpression inSubExpr:
                    isAlwaysNull = false;
                    return CompileInSubquery(inSubExpr, negated: false, CompileSub, activeTables, inserted, deleted, materializedSubqueries);

                case SqlSubqueryExpression:
                case SqlExistsExpression:
                    return CompileExpressionCore(sqlExpr, activeTables, false, inserted, deleted, materializedSubqueries, false, out isAlwaysNull);

                default:
                    throw new NotSupportedException($"Expression type '{sqlExpr.GetType().Name}' is not supported in post-aggregate context.");
            }
        }

        private Expr CompileNonAggregateWithAliases(
            SqlExpression sqlExpr,
            List<ActiveTableInfo> activeTables,
            Dictionary<string, Expr> aliasExprMap,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries)
        {
            return CompileNonAggregateWithAliasesCore(
                sqlExpr,
                activeTables,
                aliasExprMap,
                inserted,
                deleted,
                materializedSubqueries,
                out _);
        }

        private Expr CompileNonAggregateWithAliasesCore(
            SqlExpression sqlExpr,
            List<ActiveTableInfo> activeTables,
            Dictionary<string, Expr> aliasExprMap,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries,
            out bool isAlwaysNull)
        {
            Expr CompileSub(SqlExpression e, out bool subNull) =>
                CompileNonAggregateWithAliasesCore(e, activeTables, aliasExprMap, inserted, deleted, materializedSubqueries, out subNull);

            switch (sqlExpr)
            {
                case SqlColumnRef col when col.Prefix == null && aliasExprMap.TryGetValue(col.ColumnName, out var aliased):
                    isAlwaysNull = false;
                    return aliased;

                case SqlUnaryExpression unary:
                    return CompileUnaryWithNull(unary, CompileSub, activeTables, inserted, deleted, materializedSubqueries, out isAlwaysNull);

                case SqlBinaryExpression binary:
                    return CompileBinaryWithNull(binary, CompileSub, activeTables, out isAlwaysNull);

                case SqlInListExpression inListExpr:
                    isAlwaysNull = false;
                    return CompileInList(inListExpr, negated: false, CompileSub);

                case SqlInSubqueryExpression inSubExpr:
                    isAlwaysNull = false;
                    return CompileInSubquery(inSubExpr, negated: false, CompileSub, activeTables, inserted, deleted, materializedSubqueries);

                case SqlFunctionCall call:
                    return CompileNonAggregateFunctionWithNull(call, CompileSub, out isAlwaysNull);

                default:
                    return CompileExpressionCore(sqlExpr, activeTables, false, inserted, deleted, materializedSubqueries, false, out isAlwaysNull);
            }
        }

        private Expr CompileExpression(
            SqlExpression sqlExpr,
            List<ActiveTableInfo>? activeTables,
            bool isAggregating = false,
            DataFrame? inserted = null,
            DataFrame? deleted = null,
            Dictionary<object, object?>? materializedSubqueries = null,
            bool isGrouped = false)
        {
            return CompileExpressionCore(
                sqlExpr,
                activeTables,
                isAggregating,
                inserted,
                deleted,
                materializedSubqueries,
                isGrouped,
                out _);
        }

        private Expr CompileExpressionCore(
            SqlExpression sqlExpr,
            List<ActiveTableInfo>? activeTables,
            bool isAggregating,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries,
            bool isGrouped,
            out bool isAlwaysNull)
        {
            Expr CompileSub(SqlExpression e, out bool subNull) =>
                CompileExpressionCore(e, activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped, out subNull);

            switch (sqlExpr)
            {
                case SqlLiteral lit:
                    if (lit.Value == null)
                    {
                        isAlwaysNull = true;
                        return CreateNullColumnExpr();
                    }
                    isAlwaysNull = false;
                    return Expr.Lit(lit.Value);

                case SqlColumnRef col:
                    isAlwaysNull = false;
                    if (activeTables == null)
                    {
                        return Expr.Col(col.ColumnName);
                    }
                    string resolvedName = ResolveColumnName(col, activeTables);
                    return Expr.Col(resolvedName);

                case SqlSubqueryExpression subqueryExpr:
                    {
                        var subLazy = PlanQuery(subqueryExpr.SelectQuery, inserted, deleted);
                        var subDf = subLazy.Limit(1).Collect().GetAwaiter().GetResult();
                        object? val = null;
                        if (subDf.RowCount > 0 && subDf.Columns.Count > 0)
                        {
                            var firstCol = subDf.Columns[0];
                            if (firstCol.ValidityMask.IsValid(0))
                            {
                                val = firstCol.Get(0);
                            }
                        }
                        if (val == null)
                        {
                            isAlwaysNull = true;
                            return CreateNullColumnExpr();
                        }
                        isAlwaysNull = false;
                        return Expr.Lit(val);
                    }

                case SqlExistsExpression existsExpr:
                    {
                        isAlwaysNull = false;
                        var subLazy = PlanQuery(existsExpr.SelectQuery, inserted, deleted);
                        var subDf = subLazy.Limit(1).Collect().GetAwaiter().GetResult();
                        return Expr.Lit(subDf.RowCount > 0);
                    }

                case SqlInSubqueryExpression inSubqueryExpr:
                    isAlwaysNull = false;
                    return CompileInSubquery(
                        inSubqueryExpr,
                        negated: false,
                        CompileSub,
                        activeTables,
                        inserted,
                        deleted,
                        materializedSubqueries);

                case SqlInListExpression inListExpr:
                    isAlwaysNull = false;
                    return CompileInList(
                        inListExpr,
                        negated: false,
                        CompileSub);

                case SqlUnaryExpression unary:
                    return CompileUnaryWithNull(unary, CompileSub, activeTables, inserted, deleted, materializedSubqueries, out isAlwaysNull);

                case SqlBinaryExpression binary:
                    return CompileBinaryWithNull(binary, CompileSub, activeTables, out isAlwaysNull);

                case SqlFunctionCall call:
                    string fn = call.FunctionName.ToUpperInvariant();

                    if (fn == "SUM")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("SUM expects exactly 1 argument.");
                        isAlwaysNull = false;
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped).Sum();
                    }
                    if (fn == "AVG")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("AVG expects exactly 1 argument.");
                        isAlwaysNull = false;
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped).Mean();
                    }
                    if (fn == "MIN")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("MIN expects exactly 1 argument.");
                        isAlwaysNull = false;
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped).Min();
                    }
                    if (fn == "MAX")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("MAX expects exactly 1 argument.");
                        isAlwaysNull = false;
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped).Max();
                    }
                    if (fn == "COUNT")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("COUNT expects exactly 1 argument.");
                        isAlwaysNull = false;
                        var arg = call.Arguments[0];
                        if (arg is SqlStarRef)
                        {
                            if (!isGrouped || activeTables == null || activeTables.Count == 0 || activeTables[0].Metadata.Columns.Count == 0)
                            {
                                return Expr.Lit(1).Count();
                            }
                            string firstColName = ResolveColumnName(
                                new SqlColumnRef(activeTables[0].Metadata.Columns[0].Name, activeTables[0].Alias ?? activeTables[0].Name),
                                activeTables);
                            return Expr.Col(firstColName).Count();
                        }
                        return CompileExpression(arg, activeTables, isAggregating, inserted, deleted, materializedSubqueries, isGrouped).Count();
                    }

                    return CompileNonAggregateFunctionWithNull(call, CompileSub, out isAlwaysNull);

                default:
                    throw new NotSupportedException($"Expression type '{sqlExpr.GetType().Name}' is not supported.");
            }
        }

        private Expr CompileUnaryWithNull(
            SqlUnaryExpression unary,
            CompileWithNullDelegate compileSub,
            List<ActiveTableInfo>? activeTables,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries,
            out bool isAlwaysNull)
        {
            if (unary.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                if (unary.Operand is SqlInSubqueryExpression notInSub)
                {
                    isAlwaysNull = false;
                    return CompileInSubquery(notInSub, negated: true, compileSub, activeTables, inserted, deleted, materializedSubqueries);
                }
                if (unary.Operand is SqlInListExpression notInList)
                {
                    isAlwaysNull = false;
                    return CompileInList(notInList, negated: true, compileSub);
                }

                var operandExpr = compileSub(unary.Operand, out bool operandIsNull);
                isAlwaysNull = false;
                if (operandIsNull)
                {
                    return CreateUnknownBooleanExpr();
                }
                return CompileUnaryOperator(unary.Operator, operandExpr);
            }

            var opExpr = compileSub(unary.Operand, out bool opIsNull);
            if (opIsNull)
            {
                isAlwaysNull = true;
                return CreateNullColumnExpr();
            }
            isAlwaysNull = false;
            return CompileUnaryOperator(unary.Operator, opExpr);
        }

        private static Expr CompileBinaryWithNull(
            SqlBinaryExpression binary,
            CompileWithNullDelegate compileSub,
            List<ActiveTableInfo>? activeTables,
            out bool isAlwaysNull)
        {
            var left = compileSub(binary.Left, out bool leftIsNull);
            var right = compileSub(binary.Right, out bool rightIsNull);

            string op = binary.Operator.ToUpperInvariant();

            if (op == "IS")
            {
                isAlwaysNull = false;
                if (rightIsNull)
                {
                    return leftIsNull ? Expr.Lit(true) : left.IsNull();
                }
                if (leftIsNull)
                {
                    return CreateUnknownBooleanExpr();
                }
            }
            else if (op == "IS NOT")
            {
                isAlwaysNull = false;
                if (rightIsNull)
                {
                    return leftIsNull ? Expr.Lit(false) : left.IsNotNull();
                }
                if (leftIsNull)
                {
                    return CreateUnknownBooleanExpr();
                }
            }
            else if (op == "AND")
            {
                isAlwaysNull = false;
                var l = leftIsNull ? CreateUnknownBooleanExpr() : left;
                var r = rightIsNull ? CreateUnknownBooleanExpr() : right;
                return l & r;
            }
            else if (op == "OR")
            {
                isAlwaysNull = false;
                var l = leftIsNull ? CreateUnknownBooleanExpr() : left;
                var r = rightIsNull ? CreateUnknownBooleanExpr() : right;
                return l | r;
            }
            else if (op == "=" || op == "<>" || op == "!=" || op == ">" || op == "<" || op == ">=" || op == "<=")
            {
                isAlwaysNull = false;
                if (leftIsNull || rightIsNull)
                {
                    return CreateUnknownBooleanExpr();
                }
            }
            else if (op == "+" || op == "-" || op == "*" || op == "/")
            {
                if (leftIsNull || rightIsNull)
                {
                    isAlwaysNull = true;
                    return CreateNullColumnExpr();
                }
            }

            if (activeTables != null && !leftIsNull && !rightIsNull)
            {
                if (IsBitColumn(binary.Left, activeTables) && binary.Right is SqlLiteral { Value: int rBit })
                {
                    right = Expr.Lit(rBit != 0);
                }
                else if (IsBitColumn(binary.Right, activeTables) && binary.Left is SqlLiteral { Value: int lBit })
                {
                    left = Expr.Lit(lBit != 0);
                }
            }

            isAlwaysNull = false;
            return CompileBinaryOperator(binary, left, right);
        }

        private Expr CompileInSubquery(
            SqlInSubqueryExpression inSubqueryExpr,
            bool negated,
            CompileWithNullDelegate compileOperand,
            List<ActiveTableInfo>? activeTables,
            DataFrame? inserted,
            DataFrame? deleted,
            Dictionary<object, object?>? materializedSubqueries)
        {
            _ = activeTables;

            List<object> values;
            bool hasNull;

            if (materializedSubqueries != null &&
                materializedSubqueries.TryGetValue(inSubqueryExpr, out var cached) &&
                cached is MaterializedInSubqueryData data)
            {
                values = data.Values;
                hasNull = data.HasNull;
            }
            else
            {
                var subLazy = PlanQuery(inSubqueryExpr.Subquery, inserted, deleted);
                var subDf = subLazy.Collect().GetAwaiter().GetResult();

                values = new List<object>();
                hasNull = false;
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
                        var val = subCol.Get(i);
                        if (val == null)
                        {
                            hasNull = true;
                        }
                        else
                        {
                            values.Add(val);
                        }
                    }
                }
            }

            var leftCompiled = compileOperand(inSubqueryExpr.Left, out bool leftIsNull);
            if (leftIsNull)
            {
                if (values.Count == 0 && !hasNull)
                {
                    return negated ? Expr.Lit(true) : Expr.Lit(false);
                }
                return CreateUnknownBooleanExpr();
            }

            if (values.Count == 0)
            {
                if (hasNull)
                {
                    return CreateUnknownBooleanExpr();
                }
                return negated ? Expr.Lit(true) : Expr.Lit(false);
            }

            var inNonNull = leftCompiled.IsIn(values);
            if (!negated)
            {
                return hasNull ? (inNonNull | CreateUnknownBooleanExpr()) : inNonNull;
            }

            // SQL 3-valued NULL logic for NOT IN (subquery):
            // FALSE if x matches any element of S_nn; UNKNOWN if x is NULL or S contains NULL without match; TRUE otherwise.
            var notInNonNull = inNonNull == Expr.Lit(false);
            return hasNull ? (notInNonNull & CreateUnknownBooleanExpr()) : notInNonNull;
        }

        private static Expr CompileInList(
            SqlInListExpression inListExpr,
            bool negated,
            CompileWithNullDelegate compileOperand)
        {
            var leftCompiled = compileOperand(inListExpr.Left, out bool leftIsNull);

            bool allLiterals = true;
            bool hasNull = false;
            var literalValues = new List<object>(inListExpr.List.Count);
            var nonNullCompiled = new List<Expr>(inListExpr.List.Count);

            foreach (var item in inListExpr.List)
            {
                if (item is SqlLiteral lit)
                {
                    if (lit.Value == null)
                    {
                        hasNull = true;
                    }
                    else
                    {
                        literalValues.Add(lit.Value);
                        nonNullCompiled.Add(Expr.Lit(lit.Value));
                    }
                }
                else
                {
                    var itemExpr = compileOperand(item, out bool itemIsNull);
                    if (itemIsNull)
                    {
                        hasNull = true;
                    }
                    else
                    {
                        allLiterals = false;
                        nonNullCompiled.Add(itemExpr);
                    }
                }
            }

            if (leftIsNull)
            {
                if (inListExpr.List.Count == 0)
                {
                    return negated ? Expr.Lit(true) : Expr.Lit(false);
                }
                return CreateUnknownBooleanExpr();
            }

            if (allLiterals)
            {
                if (literalValues.Count == 0)
                {
                    if (hasNull)
                    {
                        return CreateUnknownBooleanExpr();
                    }
                    return negated ? Expr.Lit(true) : Expr.Lit(false);
                }

                var inNonNull = leftCompiled.IsIn(literalValues);
                if (!negated)
                {
                    return hasNull ? (inNonNull | CreateUnknownBooleanExpr()) : inNonNull;
                }

                var notInNonNull = inNonNull == Expr.Lit(false);
                return hasNull ? (notInNonNull & CreateUnknownBooleanExpr()) : notInNonNull;
            }

            if (nonNullCompiled.Count == 0)
            {
                if (hasNull)
                {
                    return CreateUnknownBooleanExpr();
                }
                return negated ? Expr.Lit(true) : Expr.Lit(false);
            }

            if (!negated)
            {
                Expr? chain = null;
                foreach (var rightCompiled in nonNullCompiled)
                {
                    var eq = leftCompiled == rightCompiled;
                    chain = chain is null ? eq : chain | eq;
                }
                Expr result = chain ?? Expr.Lit(false);
                return hasNull ? (result | CreateUnknownBooleanExpr()) : result;
            }
            else
            {
                Expr? chain = null;
                foreach (var rightCompiled in nonNullCompiled)
                {
                    var neq = leftCompiled != rightCompiled;
                    chain = chain is null ? neq : chain & neq;
                }
                Expr result = chain ?? Expr.Lit(true);
                return hasNull ? (result & CreateUnknownBooleanExpr()) : result;
            }
        }

        private static Expr CompileUnaryOperator(string op, Expr operand)
        {
            if (op.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                return operand == Expr.Lit(false);
            }
            if (op == "-")
            {
                return -operand;
            }
            if (op == "+")
            {
                return operand;
            }
            throw new NotSupportedException($"Unary operator '{op}' is not supported.");
        }

        private static Expr CompileBinaryOperator(SqlBinaryExpression binary, Expr left, Expr right)
        {
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
        }

        private static Expr CompileNonAggregateFunctionWithNull(SqlFunctionCall call, CompileWithNullDelegate compileArg, out bool isAlwaysNull)
        {
            string fn = call.FunctionName.ToUpperInvariant();

            if (fn == "LEN" || fn == "LENGTH")
            {
                if (call.Arguments.Count != 1) throw new Exception("LEN expects exactly 1 argument.");
                var argExpr = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                isAlwaysNull = false;
                return argExpr.Str().Lengths();
            }
            if (fn == "UPPER")
            {
                if (call.Arguments.Count != 1) throw new Exception("UPPER expects exactly 1 argument.");
                var argExpr = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                isAlwaysNull = false;
                return argExpr.Str().ToUppercase();
            }
            if (fn == "LOWER")
            {
                if (call.Arguments.Count != 1) throw new Exception("LOWER expects exactly 1 argument.");
                var argExpr = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                isAlwaysNull = false;
                return argExpr.Str().ToLowercase();
            }
            if (fn == "ABS")
            {
                if (call.Arguments.Count != 1) throw new Exception("ABS expects exactly 1 argument.");
                var argExpr = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                isAlwaysNull = false;
                return argExpr.Abs();
            }
            if (fn == "SQRT")
            {
                if (call.Arguments.Count != 1) throw new Exception("SQRT expects exactly 1 argument.");
                var argExpr = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                isAlwaysNull = false;
                return argExpr.Sqrt();
            }
            if (fn == "ROUND")
            {
                if (call.Arguments.Count < 1 || call.Arguments.Count > 2) throw new Exception("ROUND expects 1 or 2 arguments.");
                var target = compileArg(call.Arguments[0], out bool argNull);
                if (argNull) { isAlwaysNull = true; return CreateNullColumnExpr(); }
                int decimals = 0;
                if (call.Arguments.Count == 2)
                {
                    if (call.Arguments[1] is SqlLiteral { Value: null })
                    {
                        isAlwaysNull = true;
                        return CreateNullColumnExpr();
                    }
                    if (call.Arguments[1] is SqlLiteral l && l.Value is int i)
                    {
                        decimals = i;
                    }
                }
                isAlwaysNull = false;
                return target.Round(decimals);
            }
            if (fn == "GETDATE" || fn == "CURRENT_TIMESTAMP")
            {
                isAlwaysNull = false;
                return Expr.Lit(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            throw new NotSupportedException($"Function '{call.FunctionName}' is not supported.");
        }
    }
}
