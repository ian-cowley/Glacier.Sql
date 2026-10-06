using System;
using System.Collections.Generic;
using System.Text;

namespace Glacier.Sql.Parser
{
    public partial class TSqlParser
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

        private Token Read()
        {
            var token = Current;
            _position++;
            return token;
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
    }
}
