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

        private static bool NeedsBrackets(string identifier)
        {
            if (string.IsNullOrEmpty(identifier) || SqlLexer.IsKeyword(identifier))
            {
                return true;
            }

            char first = identifier[0];
            if (!char.IsLetter(first) && first != '_')
            {
                return true;
            }

            for (int i = 1; i < identifier.Length; i++)
            {
                char c = identifier[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    return true;
                }
            }

            return false;
        }

        private string ReconstructSqlFromTokens(int startIdx, int endIdx)
        {
            var sb = new StringBuilder();
            for (int i = startIdx; i < endIdx && i < _tokens.Count; i++)
            {
                var t = _tokens[i];
                if (t.Type == TokenType.EOF) break;

                if (t.Type == TokenType.StringLiteral)
                {
                    sb.Append('\'').Append(t.Text.Replace("'", "''")).Append('\'');
                }
                else if (t.Type == TokenType.Identifier)
                {
                    if (NeedsBrackets(t.Text))
                    {
                        sb.Append('[').Append(t.Text).Append(']');
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

                sb.Append(' ');
            }

            return sb.ToString().Trim();
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

                string dataType = ParseColumnDataType(columnName);

                bool isNullable = true;
                bool isPrimaryKey = false;
                bool isUnique = false;
                string? checkExpression = null;

                ParseInlineColumnConstraints(ref isNullable, ref isPrimaryKey, ref isUnique, ref checkExpression);

                columns.Add(new ColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, isUnique, checkExpression));

                if (Match(TokenType.Comma)) continue;
                break;
            }

            Consume(TokenType.CloseParenthesis, "Expected ')' to close column definitions");
            return new CreateTableStatement(tableName, columns);
        }

        private string ParseColumnDataType(string columnName)
        {
            if (Match(TokenType.Int)) return "INT";
            if (Match(TokenType.Float)) return "FLOAT";
            if (Match(TokenType.Varchar))
            {
                if (Match(TokenType.OpenParenthesis))
                {
                    if (Current.Type == TokenType.NumberLiteral || Current.Text.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                    {
                        Read();
                    }
                    Consume(TokenType.CloseParenthesis);
                }
                return "VARCHAR";
            }
            if (Match(TokenType.Bit)) return "BIT";
            if (Match(TokenType.Datetime)) return "DATETIME";

            throw new Exception($"Unsupported data type '{Current.Text}' for column '{columnName}'");
        }

        private void ParseInlineColumnConstraints(ref bool isNullable, ref bool isPrimaryKey, ref bool isUnique, ref string? checkExpression)
        {
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
                    _ = ParseExpression(0);
                    int checkEnd = _position;
                    Consume(TokenType.CloseParenthesis, "Expected ')' to close CHECK constraint");

                    checkExpression = ReconstructSqlFromTokens(checkStart, checkEnd);
                }
                else
                {
                    break;
                }
            }
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
            _ = Parse();
            int bodyEnd = _position;

            string actionSql = ReconstructSqlFromTokens(bodyStart, bodyEnd);
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
                Match(TokenType.Column);

                string columnName = Consume(TokenType.Identifier, "Expected column name to add").Text;
                string dataType = ParseColumnDataType(columnName);

                bool isNullable = true;
                bool isPrimaryKey = false;
                bool isUnique = false;
                string? checkExpression = null;

                ParseInlineColumnConstraints(ref isNullable, ref isPrimaryKey, ref isUnique, ref checkExpression);

                var colDef = new ColumnDefinition(columnName, dataType, isNullable, isPrimaryKey, isUnique, checkExpression);
                return new AlterTableStatement(tableName, "ADD", columnName, dataType, colDef);
            }
            else if (Match(TokenType.Drop))
            {
                Match(TokenType.Column);
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

            string definitionSql = ReconstructSqlFromTokens(selectStart, selectEnd);
            return new CreateViewStatement(viewName, selectQuery, definitionSql);
        }
    }
}
