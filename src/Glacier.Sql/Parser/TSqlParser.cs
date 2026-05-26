using System;
using System.Collections.Generic;
using System.Text;

namespace Glacier.Sql.Parser
{
    public class TSqlParser
    {
        private readonly List<Token> _tokens;
        private int _position;

        public TSqlParser(List<Token> tokens)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        private Token Current => _position >= _tokens.Count ? new Token(TokenType.EOF, "", 0, 0) : _tokens[_position];
        private Token Peek => _position + 1 >= _tokens.Count ? new Token(TokenType.EOF, "", 0, 0) : _tokens[_position + 1];

        private Token Consume(TokenType type, string? errorMessage = null)
        {
            if (Current.Type == type)
            {
                var token = Current;
                _position++;
                return token;
            }
            throw new Exception(errorMessage ?? $"Expected token of type {type} but found {Current.Type} ('{Current.Text}') at line {Current.Line}, col {Current.Column}");
        }

        private bool Match(TokenType type)
        {
            if (Current.Type == type)
            {
                _position++;
                return true;
            }
            return false;
        }

        public SqlStatement Parse()
        {
            if (Current.Type == TokenType.Select)
            {
                return ParseSelect();
            }
            if (Current.Type == TokenType.Create)
            {
                return ParseCreate();
            }
            if (Current.Type == TokenType.Drop)
            {
                return ParseDrop();
            }
            if (Current.Type == TokenType.Alter)
            {
                return ParseAlter();
            }
            if (Current.Type == TokenType.Insert)
            {
                return ParseInsert();
            }
            if (Current.Type == TokenType.Delete)
            {
                return ParseDelete();
            }
            if (Current.Type == TokenType.Update)
            {
                return ParseUpdate();
            }
            if (Current.Type == TokenType.Begin)
            {
                return ParseBegin();
            }
            if (Current.Type == TokenType.Commit)
            {
                return ParseCommit();
            }
            if (Current.Type == TokenType.Rollback)
            {
                return ParseRollback();
            }

            throw new Exception($"Unsupported statement starting with '{Current.Text}' at line {Current.Line}, col {Current.Column}");
        }

        private SqlStatement ParseDelete()
        {
            Consume(TokenType.Delete);
            if (Current.Type == TokenType.From)
            {
                Consume(TokenType.From);
            }
            string tableName = Consume(TokenType.Identifier, "Expected table name in DELETE statement").Text;

            SqlExpression? where = null;
            if (Match(TokenType.Where))
            {
                where = ParseExpression(0);
            }

            return new DeleteStatement(tableName, where);
        }

        private SqlStatement ParseUpdate()
        {
            Consume(TokenType.Update);
            string tableName = Consume(TokenType.Identifier, "Expected table name in UPDATE statement").Text;
            Consume(TokenType.Set, "Expected SET keyword in UPDATE statement");

            var assignments = new List<UpdateAssignment>();
            while (true)
            {
                string colName = Consume(TokenType.Identifier, "Expected column identifier in SET clause").Text;
                Consume(TokenType.Equal, "Expected '=' in column assignment");
                var expr = ParseExpression(0);
                assignments.Add(new UpdateAssignment(colName, expr));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            SqlExpression? where = null;
            if (Match(TokenType.Where))
            {
                where = ParseExpression(0);
            }

            return new UpdateStatement(tableName, assignments, where);
        }

        private SqlStatement ParseBegin()
        {
            Consume(TokenType.Begin);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new BeginTransactionStatement();
        }

        private SqlStatement ParseCommit()
        {
            Consume(TokenType.Commit);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new CommitTransactionStatement();
        }

        private SqlStatement ParseRollback()
        {
            Consume(TokenType.Rollback);
            if (Current.Type == TokenType.Transaction)
            {
                Consume(TokenType.Transaction);
            }
            return new RollbackTransactionStatement();
        }

        private SqlStatement ParseCreate()
        {
            Consume(TokenType.Create);
            if (Current.Type == TokenType.Trigger)
            {
                return ParseCreateTrigger();
            }
            if (Current.Type == TokenType.View)
            {
                return ParseCreateView();
            }
            Consume(TokenType.Table);
            
            string tableName = Consume(TokenType.Identifier, "Expected table name after CREATE TABLE").Text;
            Consume(TokenType.OpenParenthesis, "Expected '(' after table name");

            var columns = new List<ColumnDefinition>();
            while (true)
            {
                string columnName = Consume(TokenType.Identifier, "Expected column name").Text;
                
                // Parse Data Type
                string dataType;
                if (Match(TokenType.Int)) dataType = "INT";
                else if (Match(TokenType.Float)) dataType = "FLOAT";
                else if (Match(TokenType.Varchar))
                {
                    dataType = "VARCHAR";
                    if (Match(TokenType.OpenParenthesis))
                    {
                        // consume length or MAX
                        if (Current.Type == TokenType.NumberLiteral || Current.Text.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                        {
                            Read(); // consume number/MAX
                        }
                        Consume(TokenType.CloseParenthesis);
                    }
                }
                else if (Match(TokenType.Bit)) dataType = "BIT";
                else if (Match(TokenType.Datetime)) dataType = "DATETIME";
                else
                {
                    throw new Exception($"Unsupported data type '{Current.Text}' for column '{columnName}'");
                }

                bool isNullable = true;
                bool isPrimaryKey = false;
                bool isUnique = false;
                string? checkExpression = null;

                while (true)
                {
                    if (Match(TokenType.Null))
                    {
                        isNullable = true;
                    }
                    else if (Match(TokenType.Not))
                    {
                        Consume(TokenType.Null, "Expected NULL after NOT");
                        isNullable = false;
                    }
                    else if (Match(TokenType.Primary))
                    {
                        Consume(TokenType.Key, "Expected KEY after PRIMARY");
                        isPrimaryKey = true;
                        isNullable = false;
                    }
                    else if (Match(TokenType.Unique))
                    {
                        isUnique = true;
                    }
                    else if (Match(TokenType.Check))
                    {
                        Consume(TokenType.OpenParenthesis, "Expected '(' after CHECK");
                        int checkStart = _position;
                        var expr = ParseExpression(0);
                        int checkEnd = _position;
                        Consume(TokenType.CloseParenthesis, "Expected ')' to close CHECK constraint");

                        var sbCheck = new StringBuilder();
                        for (int i = checkStart; i < checkEnd; i++)
                        {
                            var t = _tokens[i];
                            if (t.Type == TokenType.EOF) break;
                            if (t.Type == TokenType.StringLiteral) sbCheck.Append("'").Append(t.Text.Replace("'", "''")).Append("'");
                            else if (t.Type == TokenType.Identifier) sbCheck.Append(t.Text);
                            else sbCheck.Append(t.Text);
                            sbCheck.Append(" ");
                        }
                        checkExpression = sbCheck.ToString().Trim();
                    }
                    else
                    {
                        break;
                    }
                }

                columns.Add(new ColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, isUnique, checkExpression));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            Consume(TokenType.CloseParenthesis, "Expected ')' to close column definitions");
            return new CreateTableStatement(tableName, columns);
        }

        private SqlStatement ParseCreateTrigger()
        {
            Consume(TokenType.Trigger);
            string triggerName = Consume(TokenType.Identifier, "Expected trigger name after CREATE TRIGGER").Text;
            Consume(TokenType.On, "Expected ON after trigger name");
            string tableName = Consume(TokenType.Identifier, "Expected table name after ON").Text;

            string timing;
            if (Match(TokenType.After))
            {
                timing = "AFTER";
            }
            else if (Match(TokenType.Instead))
            {
                Consume(TokenType.Of, "Expected OF after INSTEAD");
                timing = "INSTEAD OF";
            }
            else
            {
                throw new Exception("Expected AFTER or INSTEAD OF in CREATE TRIGGER");
            }

            string eventType;
            if (Match(TokenType.Insert)) eventType = "INSERT";
            else if (Match(TokenType.Update)) eventType = "UPDATE";
            else if (Match(TokenType.Delete)) eventType = "DELETE";
            else throw new Exception("Expected INSERT, UPDATE, or DELETE event type in CREATE TRIGGER");

            Consume(TokenType.As, "Expected AS keyword before trigger body");

            int bodyStart = _position;
            // Parse the inner statement to validate it
            var actionStmt = Parse();
            int bodyEnd = _position;

            var sb = new StringBuilder();
            for (int i = bodyStart; i < bodyEnd; i++)
            {
                var t = _tokens[i];
                if (t.Type == TokenType.EOF) break;
                if (t.Type == TokenType.StringLiteral)
                {
                    sb.Append("'").Append(t.Text.Replace("'", "''")).Append("'");
                }
                else if (t.Type == TokenType.Identifier)
                {
                    if (t.Text.Contains(" ") || t.Text.Contains(".") || t.Text.Contains("-"))
                    {
                        sb.Append("[").Append(t.Text).Append("]");
                    }
                    else
                    {
                        sb.Append(t.Text);
                    }
                }
                else
                {
                    sb.Append(t.Text);
                }
                sb.Append(" ");
            }
            string actionSql = sb.ToString().Trim();

            return new CreateTriggerStatement(triggerName, tableName, eventType, timing, actionSql);
        }

        private SqlStatement ParseDrop()
        {
            Consume(TokenType.Drop);
            if (Match(TokenType.View))
            {
                string viewName = Consume(TokenType.Identifier, "Expected view name after DROP VIEW").Text;
                return new DropViewStatement(viewName);
            }
            Consume(TokenType.Table);
            string tableName = Consume(TokenType.Identifier, "Expected table name after DROP TABLE").Text;
            return new DropTableStatement(tableName);
        }

        private SqlStatement ParseAlter()
        {
            Consume(TokenType.Alter);
            Consume(TokenType.Table);
            string tableName = Consume(TokenType.Identifier, "Expected table name after ALTER TABLE").Text;

            if (Match(TokenType.Add))
            {
                if (Match(TokenType.Column))
                {
                }
                
                string columnName = Consume(TokenType.Identifier, "Expected column name to add").Text;
                
                // Parse Data Type
                string dataType;
                if (Match(TokenType.Int)) dataType = "INT";
                else if (Match(TokenType.Float)) dataType = "FLOAT";
                else if (Match(TokenType.Varchar))
                {
                    dataType = "VARCHAR";
                    if (Match(TokenType.OpenParenthesis))
                    {
                        if (Current.Type == TokenType.NumberLiteral || Current.Text.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                        {
                            Read();
                        }
                        Consume(TokenType.CloseParenthesis);
                    }
                }
                else if (Match(TokenType.Bit)) dataType = "BIT";
                else if (Match(TokenType.Datetime)) dataType = "DATETIME";
                else
                {
                    throw new Exception($"Unsupported data type '{Current.Text}' for new column '{columnName}'");
                }

                bool isNullable = true;
                bool isPrimaryKey = false;
                bool isUnique = false;
                string? checkExpression = null;

                while (true)
                {
                    if (Match(TokenType.Null))
                    {
                        isNullable = true;
                    }
                    else if (Match(TokenType.Not))
                    {
                        Consume(TokenType.Null, "Expected NULL after NOT");
                        isNullable = false;
                    }
                    else if (Match(TokenType.Primary))
                    {
                        Consume(TokenType.Key, "Expected KEY after PRIMARY");
                        isPrimaryKey = true;
                        isNullable = false;
                    }
                    else if (Match(TokenType.Unique))
                    {
                        isUnique = true;
                    }
                    else if (Match(TokenType.Check))
                    {
                        Consume(TokenType.OpenParenthesis, "Expected '(' after CHECK");
                        int checkStart = _position;
                        var expr = ParseExpression(0);
                        int checkEnd = _position;
                        Consume(TokenType.CloseParenthesis, "Expected ')' to close CHECK constraint");

                        var sbCheck = new StringBuilder();
                        for (int i = checkStart; i < checkEnd; i++)
                        {
                            var t = _tokens[i];
                            if (t.Type == TokenType.EOF) break;
                            if (t.Type == TokenType.StringLiteral) sbCheck.Append("'").Append(t.Text.Replace("'", "''")).Append("'");
                            else if (t.Type == TokenType.Identifier) sbCheck.Append(t.Text);
                            else sbCheck.Append(t.Text);
                            sbCheck.Append(" ");
                        }
                        checkExpression = sbCheck.ToString().Trim();
                    }
                    else
                    {
                        break;
                    }
                }

                var colDef = new ColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, isUnique, checkExpression);
                return new AlterTableStatement(tableName, "ADD", columnName, dataType, colDef);
            }
            else if (Match(TokenType.Drop))
            {
                if (Match(TokenType.Column))
                {
                }
                string columnName = Consume(TokenType.Identifier, "Expected column name to drop").Text;
                return new AlterTableStatement(tableName, "DROP", columnName, null);
            }
            else
            {
                throw new Exception("Expected ADD or DROP keyword in ALTER TABLE statement");
            }
        }

        private SqlStatement ParseCreateView()
        {
            Consume(TokenType.View);
            string viewName = Consume(TokenType.Identifier, "Expected view name after CREATE VIEW").Text;
            Consume(TokenType.As, "Expected AS keyword after view name");

            int selectStart = _position;
            var selectQuery = (SelectStatement)ParseSelect();
            int selectEnd = _position;

            var sbSelect = new StringBuilder();
            for (int i = selectStart; i < selectEnd; i++)
            {
                var t = _tokens[i];
                if (t.Type == TokenType.EOF) break;
                if (t.Type == TokenType.StringLiteral) sbSelect.Append("'").Append(t.Text.Replace("'", "''")).Append("'");
                else if (t.Type == TokenType.Identifier) sbSelect.Append(t.Text);
                else sbSelect.Append(t.Text);
                sbSelect.Append(" ");
            }
            string definitionSql = sbSelect.ToString().Trim();

            return new CreateViewStatement(viewName, selectQuery, definitionSql);
        }

        private SqlStatement ParseInsert()
        {
            Consume(TokenType.Insert);
            Consume(TokenType.Into);

            string tableName = Consume(TokenType.Identifier, "Expected table name after INSERT INTO").Text;

            List<string>? columns = null;
            if (Match(TokenType.OpenParenthesis))
            {
                columns = new List<string>();
                while (true)
                {
                    columns.Add(Consume(TokenType.Identifier, "Expected column identifier").Text);
                    if (Match(TokenType.Comma)) continue;
                    break;
                }
                Consume(TokenType.CloseParenthesis, "Expected ')' after insert column list");
            }

            if (Current.Type == TokenType.Select)
            {
                var selectQuery = (SelectStatement)ParseSelect();
                return new InsertSelectStatement(tableName, columns, selectQuery);
            }

            Consume(TokenType.Values, "Expected VALUES keyword");
            Consume(TokenType.OpenParenthesis, "Expected '(' before VALUES list");

            var values = new List<SqlExpression>();
            while (true)
            {
                values.Add(ParseExpression(0));
                if (Match(TokenType.Comma)) continue;
                break;
            }

            Consume(TokenType.CloseParenthesis, "Expected ')' to close VALUES list");
            return new InsertStatement(tableName, columns, values);
        }

        private SqlStatement ParseSelect()
        {
            Consume(TokenType.Select);
            var stmt = new SelectStatement();

            // TOP
            if (Match(TokenType.Top))
            {
                var numToken = Consume(TokenType.NumberLiteral, "Expected number after TOP clause");
                stmt.Top = int.Parse(numToken.Text);
            }

            // Projections
            while (true)
            {
                var expr = ParseExpression(0);
                string? alias = null;
                
                if (Match(TokenType.As))
                {
                    alias = Consume(TokenType.Identifier, "Expected alias identifier after AS").Text;
                }
                else if (Current.Type == TokenType.Identifier)
                {
                    alias = Consume(TokenType.Identifier).Text;
                }

                stmt.Projections.Add(new SelectItem(expr, alias));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            // FROM
            if (Match(TokenType.From))
            {
                stmt.From = ParseTableRef();

                // JOINS
                while (true)
                {
                    string? joinType = null;
                    if (Match(TokenType.Inner))
                    {
                        Consume(TokenType.Join);
                        joinType = "INNER";
                    }
                    else if (Match(TokenType.Left))
                    {
                        if (Current.Type == TokenType.Join) Consume(TokenType.Join);
                        joinType = "LEFT";
                    }
                    else if (Match(TokenType.Cross))
                    {
                        Consume(TokenType.Join);
                        joinType = "CROSS";
                    }
                    else if (Current.Type == TokenType.Join)
                    {
                        Consume(TokenType.Join);
                        joinType = "INNER"; // default JOIN is INNER
                    }

                    if (joinType == null) break;

                    var tableRef = ParseTableRef();
                    var join = new SqlJoin(tableRef, joinType);

                    if (joinType != "CROSS")
                    {
                        Consume(TokenType.On, "Expected ON clause for join");
                        join.On = ParseExpression(0);
                    }

                    stmt.Joins.Add(join);
                }

                // WHERE
                if (Match(TokenType.Where))
                {
                    stmt.Where = ParseExpression(0);
                }

                // GROUP BY
                if (Match(TokenType.Group))
                {
                    Consume(TokenType.By, "Expected BY after GROUP");
                    while (true)
                    {
                        stmt.GroupBy.Add(ParseExpression(0));
                        if (Match(TokenType.Comma)) continue;
                        break;
                    }
                }

                // HAVING
                if (Match(TokenType.Having))
                {
                    stmt.Having = ParseExpression(0);
                }

                // ORDER BY
                if (Match(TokenType.Order))
                {
                    Consume(TokenType.By, "Expected BY after ORDER");
                    while (true)
                    {
                        var expr = ParseExpression(0);
                        bool desc = false;
                        if (Match(TokenType.Desc)) desc = true;
                        else if (Match(TokenType.Asc)) desc = false;

                        stmt.OrderBy.Add(new SqlOrderBy(expr, desc));

                        if (Match(TokenType.Comma)) continue;
                        break;
                    }
                }
            }

            return stmt;
        }

        private SqlTableRef ParseTableRef()
        {
            string tableName = Consume(TokenType.Identifier, "Expected table name in FROM clause").Text;
            while (Match(TokenType.Dot))
            {
                tableName += "." + Consume(TokenType.Identifier, "Expected identifier after '.'").Text;
            }
            string? alias = null;
            if (Match(TokenType.As))
            {
                alias = Consume(TokenType.Identifier, "Expected table alias").Text;
            }
            else if (Current.Type == TokenType.Identifier && Current.Type != TokenType.Join && 
                     Current.Type != TokenType.Inner && Current.Type != TokenType.Left && Current.Type != TokenType.Cross &&
                     Current.Type != TokenType.Where && Current.Type != TokenType.Group && Current.Type != TokenType.Order)
            {
                alias = Consume(TokenType.Identifier).Text;
            }

            return new SqlTableSource(tableName, alias);
        }

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
                    if (token.Text.Contains(".")) return new SqlLiteral(double.Parse(token.Text, System.Globalization.CultureInfo.InvariantCulture));
                    return new SqlLiteral(int.Parse(token.Text));

                case TokenType.StringLiteral:
                    return new SqlLiteral(token.Text);

                case TokenType.Star:
                    return new SqlStarRef(null);

                case TokenType.Identifier:
                    // Check if it's a function call (identifier followed by open parenthesis)
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
                    
                    // Check for dotted identifiers (table.column or table.*)
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

                case TokenType.Minus:
                    // Unary negate
                    var operand = ParseExpression(6); // high precedence for unary minus
                    return new SqlUnaryExpression("-", operand);

                case TokenType.Not:
                    // Unary logical NOT
                    var logOperand = ParseExpression(2); // Logical NOT precedence
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

            // Handle IS NOT as a single operator
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

        private Token Read()
        {
            var token = Current;
            _position++;
            return token;
        }
    }
}
