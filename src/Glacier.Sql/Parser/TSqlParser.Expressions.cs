using System;
using System.Collections.Generic;
using System.Globalization;

namespace Glacier.Sql.Parser
{
    public partial class TSqlParser
    {
        // Pratt Parsing Precedence Levels
        private static readonly Dictionary<TokenType, int> InfixPrecedences = new()
        {
            { TokenType.Or, 1 },
            { TokenType.And, 2 },
            { TokenType.Equal, 3 },
            { TokenType.NotEqual, 3 },
            { TokenType.GreaterThan, 3 },
            { TokenType.LessThan, 3 },
            { TokenType.GreaterOrEqual, 3 },
            { TokenType.LessOrEqual, 3 },
            { TokenType.Is, 3 },
            { TokenType.In, 3 },
            { TokenType.Plus, 4 },
            { TokenType.Minus, 4 },
            { TokenType.Star, 5 },
            { TokenType.Divide, 5 }
        };

        private int GetPrecedence(TokenType type)
        {
            if (InfixPrecedences.TryGetValue(type, out int precedence)) return precedence;
            return 0;
        }

        public SqlExpression ParseExpression(int precedence)
        {
            var left = ParsePrefixExpression();

            while (precedence < GetPrecedence(Current.Type))
            {
                left = ParseInfixExpression(left);
            }

            return left;
        }

        private SqlExpression ParsePrefixExpression()
        {
            var token = Read();

            switch (token.Type)
            {
                case TokenType.Null:
                    return new SqlLiteral(null);

                case TokenType.BooleanLiteral:
                    return new SqlLiteral(token.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase));

                case TokenType.NumberLiteral:
                    if (token.Text.Contains('.'))
                    {
                        return new SqlLiteral(double.Parse(token.Text, CultureInfo.InvariantCulture));
                    }
                    if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal))
                    {
                        if (longVal >= int.MinValue && longVal <= int.MaxValue)
                        {
                            return new SqlLiteral((int)longVal);
                        }
                        return new SqlLiteral(longVal);
                    }
                    return new SqlLiteral(double.Parse(token.Text, CultureInfo.InvariantCulture));

                case TokenType.StringLiteral:
                    return new SqlLiteral(token.Text);

                case TokenType.Star:
                    return new SqlStarRef(null);

                case TokenType.Identifier:
                    if (Current.Type == TokenType.OpenParenthesis)
                    {
                        Consume(TokenType.OpenParenthesis);
                        var args = new List<SqlExpression>();
                        if (Current.Type != TokenType.CloseParenthesis)
                        {
                            while (true)
                            {
                                args.Add(ParseExpression(0));
                                if (Match(TokenType.Comma)) continue;
                                break;
                            }
                        }
                        Consume(TokenType.CloseParenthesis, "Expected ')' at end of function arguments");
                        return new SqlFunctionCall(token.Text, args);
                    }

                    if (Match(TokenType.Dot))
                    {
                        if (Match(TokenType.Star))
                        {
                            return new SqlStarRef(token.Text);
                        }
                        var colName = Consume(TokenType.Identifier, "Expected column identifier after dot").Text;
                        return new SqlColumnRef(colName, token.Text);
                    }
                    return new SqlColumnRef(token.Text);

                case TokenType.OpenParenthesis:
                    if (Current.Type == TokenType.Select)
                    {
                        var sub = (SelectStatement)ParseSelect();
                        Consume(TokenType.CloseParenthesis);
                        return new SqlSubqueryExpression(sub);
                    }
                    var expr = ParseExpression(0);
                    Consume(TokenType.CloseParenthesis, "Expected ')' after nested expression");
                    return expr;

                case TokenType.Exists:
                    Consume(TokenType.OpenParenthesis, "Expected '(' after EXISTS");
                    var existsSub = (SelectStatement)ParseSelect();
                    Consume(TokenType.CloseParenthesis, "Expected ')' after EXISTS subquery");
                    return new SqlExistsExpression(existsSub);

                case TokenType.Plus:
                    var plusOperand = ParseExpression(6);
                    if (plusOperand is SqlLiteral plusLit && plusLit.Value is int or long or double)
                    {
                        return plusLit;
                    }
                    return new SqlUnaryExpression("+", plusOperand);

                case TokenType.Minus:
                    var operand = ParseExpression(6);
                    if (operand is SqlLiteral minusLit)
                    {
                        if (minusLit.Value is int mi)
                        {
                            long negInt = -(long)mi;
                            return new SqlLiteral(negInt >= int.MinValue && negInt <= int.MaxValue ? (object)(int)negInt : negInt);
                        }
                        if (minusLit.Value is long ml)
                        {
                            if (ml == 2147483648L)
                            {
                                return new SqlLiteral(int.MinValue);
                            }
                            if (ml != long.MinValue)
                            {
                                long negLong = -ml;
                                return new SqlLiteral(negLong >= int.MinValue && negLong <= int.MaxValue ? (object)(int)negLong : negLong);
                            }
                            return new SqlLiteral(-(double)ml);
                        }
                        if (minusLit.Value is double md)
                        {
                            return new SqlLiteral(-md);
                        }
                    }
                    return new SqlUnaryExpression("-", operand);

                case TokenType.Not:
                    var logOperand = ParseExpression(2);
                    return new SqlUnaryExpression("NOT", logOperand);

                default:
                    throw new Exception($"Unexpected token '{token.Text}' in expression at line {token.Line}, col {token.Column}");
            }
        }

        private SqlExpression ParseInfixExpression(SqlExpression left)
        {
            var opToken = Read();
            int prec = GetPrecedence(opToken.Type);

            string opStr = opToken.Text;

            if (opToken.Type == TokenType.Is && Match(TokenType.Not))
            {
                opStr = "IS NOT";
            }

            if (opToken.Type == TokenType.In)
            {
                Consume(TokenType.OpenParenthesis, "Expected '(' after IN");
                if (Current.Type == TokenType.Select)
                {
                    var sub = (SelectStatement)ParseSelect();
                    Consume(TokenType.CloseParenthesis);
                    return new SqlInSubqueryExpression(left, sub);
                }
                else
                {
                    var list = new List<SqlExpression>();
                    while (true)
                    {
                        list.Add(ParseExpression(0));
                        if (Match(TokenType.Comma)) continue;
                        break;
                    }
                    Consume(TokenType.CloseParenthesis);
                    return new SqlInListExpression(left, list);
                }
            }

            var right = ParseExpression(prec);
            return new SqlBinaryExpression(left, opStr, right);
        }
    }
}
