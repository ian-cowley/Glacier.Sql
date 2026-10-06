using System;
using System.Collections.Generic;
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
    }
}
