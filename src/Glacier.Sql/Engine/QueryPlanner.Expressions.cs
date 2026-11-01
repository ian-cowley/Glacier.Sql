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
            var activeTables = new List<(string Name, string? Alias, TableMetadata Metadata)>
            {
                (tableName, null, tableMeta)
            };
            return CompileExpression(expr, activeTables);
        }

        private static bool IsAggregateFunction(SqlFunctionCall call)
        {
            string fn = call.FunctionName.ToUpperInvariant();
            return fn == "SUM" || fn == "AVG" || fn == "MIN" || fn == "MAX" || fn == "COUNT";
        }

        private bool IsBitColumn(SqlExpression expr, List<(string Name, string? Alias, TableMetadata Metadata)> activeTables)
        {
            if (expr is not SqlColumnRef colRef) return false;
            foreach (var (name, alias, meta) in activeTables)
            {
                if (colRef.Prefix != null &&
                    !colRef.Prefix.Equals(alias ?? name, StringComparison.OrdinalIgnoreCase) &&
                    !colRef.Prefix.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colMeta = meta.Columns.FirstOrDefault(c => c.Name.Equals(colRef.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (colMeta != null)
                {
                    string dt = colMeta.DataType.ToUpperInvariant();
                    return dt == "BIT" || dt == "BOOLEAN";
                }
            }
            return false;
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
            List<(string Name, string? Alias, TableMetadata Metadata)> activeTables)
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
            List<(string Name, string? Alias, TableMetadata Metadata)> activeTables)
        {
            var argsKey = string.Join(",", call.Arguments.Select(a => FormatCanonicalExpr(a, activeTables)));
            return $"{call.FunctionName.ToUpperInvariant()}({argsKey})";
        }

        private string FormatCanonicalExpr(
            SqlExpression expr,
            List<(string Name, string? Alias, TableMetadata Metadata)> activeTables)
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
            List<(string Name, string? Alias, TableMetadata Metadata)> activeTables,
            Dictionary<string, string> keyToTempAlias,
            Dictionary<string, Expr>? aliasExprMap,
            HashSet<string> groupColumnsSet,
            DataFrame? inserted,
            DataFrame? deleted)
        {
            switch (sqlExpr)
            {
                case SqlLiteral lit:
                    return Expr.Lit(lit.Value!);

                case SqlColumnRef col:
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
                    string key = GetAggregateCanonicalKey(call, activeTables);
                    if (!keyToTempAlias.TryGetValue(key, out var tempAlias))
                    {
                        throw new Exception($"Internal planner error: aggregate '{call}' was not extracted.");
                    }
                    return Expr.Col(tempAlias);

                case SqlFunctionCall call:
                    return CompileNonAggregateFunction(
                        call,
                        arg => CompilePostAggregateExpression(arg, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted));

                case SqlUnaryExpression unary:
                    var operand = CompilePostAggregateExpression(unary.Operand, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                    return CompileUnaryOperator(unary.Operator, operand);

                case SqlBinaryExpression binary:
                    var left = CompilePostAggregateExpression(binary.Left, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                    var right = CompilePostAggregateExpression(binary.Right, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                    return CompileBinaryOperator(binary, left, right);

                case SqlInListExpression inListExpr:
                    var leftCompiled = CompilePostAggregateExpression(inListExpr.Left, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                    Expr? chain = null;
                    foreach (var exprVal in inListExpr.List)
                    {
                        var rightCompiled = CompilePostAggregateExpression(exprVal, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                        var eq = leftCompiled == rightCompiled;
                        chain = chain is null ? eq : chain | eq;
                    }
                    return chain ?? Expr.Lit(false);

                case SqlSubqueryExpression:
                case SqlExistsExpression:
                case SqlInSubqueryExpression:
                    return CompileExpression(sqlExpr, activeTables, false, inserted, deleted);

                default:
                    throw new NotSupportedException($"Expression type '{sqlExpr.GetType().Name}' is not supported in post-aggregate context.");
            }
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
                                    var eq = leftCompiled == Expr.Lit(val);
                                    chain = chain is null ? eq : chain | eq;
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
                            chain = chain is null ? eq : chain | eq;
                        }
                        return chain ?? Expr.Lit(false);
                    }

                case SqlUnaryExpression unary:
                    var operand = CompileExpression(unary.Operand, activeTables, isAggregating, inserted, deleted);
                    return CompileUnaryOperator(unary.Operator, operand);

                case SqlBinaryExpression binary:
                    var left = CompileExpression(binary.Left, activeTables, isAggregating, inserted, deleted);
                    var right = CompileExpression(binary.Right, activeTables, isAggregating, inserted, deleted);
                    if (activeTables != null)
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
                    return CompileBinaryOperator(binary, left, right);

                case SqlFunctionCall call:
                    string fn = call.FunctionName.ToUpperInvariant();

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
                            if (activeTables == null || activeTables.Count == 0 || activeTables[0].Metadata.Columns.Count == 0)
                            {
                                return Expr.Lit(1).Count();
                            }
                            string firstColName = activeTables[0].Metadata.Columns[0].Name;
                            return Expr.Col(firstColName).Count();
                        }
                        return CompileExpression(arg, activeTables, isAggregating, inserted, deleted).Count();
                    }

                    return CompileNonAggregateFunction(
                        call,
                        arg => CompileExpression(arg, activeTables, isAggregating, inserted, deleted));

                default:
                    throw new NotSupportedException($"Expression type '{sqlExpr.GetType().Name}' is not supported.");
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

        private static Expr CompileNonAggregateFunction(SqlFunctionCall call, Func<SqlExpression, Expr> compileArg)
        {
            string fn = call.FunctionName.ToUpperInvariant();

            if (fn == "LEN" || fn == "LENGTH")
            {
                if (call.Arguments.Count != 1) throw new Exception("LEN expects exactly 1 argument.");
                return compileArg(call.Arguments[0]).Str().Lengths();
            }
            if (fn == "UPPER")
            {
                if (call.Arguments.Count != 1) throw new Exception("UPPER expects exactly 1 argument.");
                return compileArg(call.Arguments[0]).Str().ToUppercase();
            }
            if (fn == "LOWER")
            {
                if (call.Arguments.Count != 1) throw new Exception("LOWER expects exactly 1 argument.");
                return compileArg(call.Arguments[0]).Str().ToLowercase();
            }
            if (fn == "ABS")
            {
                if (call.Arguments.Count != 1) throw new Exception("ABS expects exactly 1 argument.");
                return compileArg(call.Arguments[0]).Abs();
            }
            if (fn == "SQRT")
            {
                if (call.Arguments.Count != 1) throw new Exception("SQRT expects exactly 1 argument.");
                return compileArg(call.Arguments[0]).Sqrt();
            }
            if (fn == "ROUND")
            {
                if (call.Arguments.Count < 1 || call.Arguments.Count > 2) throw new Exception("ROUND expects 1 or 2 arguments.");
                var target = compileArg(call.Arguments[0]);
                int decimals = 0;
                if (call.Arguments.Count == 2 && call.Arguments[1] is SqlLiteral l && l.Value is int i)
                {
                    decimals = i;
                }
                return target.Round(decimals);
            }
            if (fn == "GETDATE" || fn == "CURRENT_TIMESTAMP")
            {
                return Expr.Lit(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            throw new NotSupportedException($"Function '{call.FunctionName}' is not supported.");
        }
    }
}
