using System;
using System.Collections.Generic;
using System.Text;

namespace Glacier.Sql.Parser
{
    public enum TokenType
    {
        // Keywords
        Select, From, Join, Inner, Left, Cross, On, Where, Group, By, Having, Order, Asc, Desc, Top, As, And, Or, Not, Is, Null, Create, Table, Drop, Insert, Into, Values, Update, Set, Delete, Begin, Transaction, Commit, Rollback,
        Trigger, After, Instead, Of,
        Alter, Add, Column, View, Unique, Check, Primary, Key, In, Exists, Constraint,
        
        // Data Types
        Int, Float, Varchar, Bit, Datetime,
        
        // Identifiers & Literals
        Identifier, StringLiteral, NumberLiteral, BooleanLiteral,
        
        // Symbols & Operators
        Equal, NotEqual, GreaterThan, LessThan, GreaterOrEqual, LessOrEqual,
        Plus, Minus, Multiply, Divide,
        OpenParenthesis, CloseParenthesis, Comma, Dot, Star,
        
        // Special
        EOF, Invalid
    }

    public record Token(TokenType Type, string Text, int Line, int Column);

    public class SqlLexer
    {
        private readonly string _input;
        private int _position;
        private int _line = 1;
        private int _column = 1;

        private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            { "SELECT", TokenType.Select },
            { "FROM", TokenType.From },
            { "JOIN", TokenType.Join },
            { "INNER", TokenType.Inner },
            { "LEFT", TokenType.Left },
            { "CROSS", TokenType.Cross },
            { "ON", TokenType.On },
            { "WHERE", TokenType.Where },
            { "GROUP", TokenType.Group },
            { "BY", TokenType.By },
            { "HAVING", TokenType.Having },
            { "ORDER", TokenType.Order },
            { "ASC", TokenType.Asc },
            { "DESC", TokenType.Desc },
            { "TOP", TokenType.Top },
            { "AS", TokenType.As },
            { "AND", TokenType.And },
            { "OR", TokenType.Or },
            { "NOT", TokenType.Not },
            { "IS", TokenType.Is },
            { "NULL", TokenType.Null },
            { "CREATE", TokenType.Create },
            { "TABLE", TokenType.Table },
            { "DROP", TokenType.Drop },
            { "INSERT", TokenType.Insert },
            { "INTO", TokenType.Into },
            { "VALUES", TokenType.Values },
            { "UPDATE", TokenType.Update },
            { "SET", TokenType.Set },
            { "DELETE", TokenType.Delete },
            { "BEGIN", TokenType.Begin },
            { "TRANSACTION", TokenType.Transaction },
            { "TRAN", TokenType.Transaction },
            { "COMMIT", TokenType.Commit },
            { "ROLLBACK", TokenType.Rollback },
            { "TRIGGER", TokenType.Trigger },
            { "AFTER", TokenType.After },
            { "INSTEAD", TokenType.Instead },
            { "OF", TokenType.Of },
            { "ALTER", TokenType.Alter },
            { "ADD", TokenType.Add },
            { "COLUMN", TokenType.Column },
            { "VIEW", TokenType.View },
            { "UNIQUE", TokenType.Unique },
            { "CHECK", TokenType.Check },
            { "PRIMARY", TokenType.Primary },
            { "KEY", TokenType.Key },
            { "IN", TokenType.In },
            { "EXISTS", TokenType.Exists },
            { "CONSTRAINT", TokenType.Constraint },
            
            // Types
            { "INT", TokenType.Int },
            { "INTEGER", TokenType.Int },
            { "FLOAT", TokenType.Float },
            { "REAL", TokenType.Float },
            { "DOUBLE", TokenType.Float },
            { "VARCHAR", TokenType.Varchar },
            { "NVARCHAR", TokenType.Varchar },
            { "TEXT", TokenType.Varchar },
            { "BIT", TokenType.Bit },
            { "BOOLEAN", TokenType.Bit },
            { "DATETIME", TokenType.Datetime },
            { "DATE", TokenType.Datetime },
            
            // Boolean values
            { "TRUE", TokenType.BooleanLiteral },
            { "FALSE", TokenType.BooleanLiteral }
        };

        public SqlLexer(string input)
        {
            _input = input ?? "";
        }

        private char Peek() => _position >= _input.Length ? '\0' : _input[_position];

        private char ReadChar()
        {
            if (_position >= _input.Length) return '\0';
            char c = _input[_position++];
            if (c == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            return c;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                var token = NextToken();
                tokens.Add(token);
                if (token.Type == TokenType.EOF) break;
            }
            return tokens;
        }

        private Token NextToken()
        {
            SkipWhitespaceAndComments();

            int startLine = _line;
            int startColumn = _column;

            if (_position >= _input.Length)
            {
                return new Token(TokenType.EOF, "", startLine, startColumn);
            }

            char c = Peek();

            // Identifiers with brackets [Column Name]
            if (c == '[')
            {
                ReadChar(); // consume '['
                var sb = new StringBuilder();
                while (Peek() != ']' && Peek() != '\0')
                {
                    sb.Append(ReadChar());
                }
                if (Peek() == ']')
                {
                    ReadChar(); // consume ']'
                }
                return new Token(TokenType.Identifier, sb.ToString(), startLine, startColumn);
            }

            // String literals 'String Value'
            if (c == '\'')
            {
                ReadChar(); // consume opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    char next = Peek();
                    if (next == '\0') break; // unterminated string
                    if (next == '\'')
                    {
                        ReadChar(); // consume quote
                        if (Peek() == '\'')
                        {
                            // Escaped quote: '' -> '
                            sb.Append(ReadChar());
                        }
                        else
                        {
                            break; // end of string
                        }
                    }
                    else
                    {
                        sb.Append(ReadChar());
                    }
                }
                return new Token(TokenType.StringLiteral, sb.ToString(), startLine, startColumn);
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(PeekAhead(1))))
            {
                var sb = new StringBuilder();
                bool hasDot = false;
                while (char.IsDigit(Peek()) || (Peek() == '.' && !hasDot))
                {
                    if (Peek() == '.') hasDot = true;
                    sb.Append(ReadChar());
                }
                return new Token(TokenType.NumberLiteral, sb.ToString(), startLine, startColumn);
            }

            // Identifiers / Keywords
            if (char.IsLetter(c) || c == '_')
            {
                var sb = new StringBuilder();
                while (char.IsLetterOrDigit(Peek()) || Peek() == '_')
                {
                    sb.Append(ReadChar());
                }
                string text = sb.ToString();
                if (Keywords.TryGetValue(text, out var type))
                {
                    return new Token(type, text, startLine, startColumn);
                }
                return new Token(TokenType.Identifier, text, startLine, startColumn);
            }

            // Multi-character Operators
            if (c == '<')
            {
                ReadChar();
                if (Peek() == '>')
                {
                    ReadChar();
                    return new Token(TokenType.NotEqual, "<>", startLine, startColumn);
                }
                if (Peek() == '=')
                {
                    ReadChar();
                    return new Token(TokenType.LessOrEqual, "<=", startLine, startColumn);
                }
                return new Token(TokenType.LessThan, "<", startLine, startColumn);
            }

            if (c == '>')
            {
                ReadChar();
                if (Peek() == '=')
                {
                    ReadChar();
                    return new Token(TokenType.GreaterOrEqual, ">=", startLine, startColumn);
                }
                return new Token(TokenType.GreaterThan, ">", startLine, startColumn);
            }

            if (c == '!')
            {
                ReadChar();
                if (Peek() == '=')
                {
                    ReadChar();
                    return new Token(TokenType.NotEqual, "!=", startLine, startColumn);
                }
                return new Token(TokenType.Invalid, "!", startLine, startColumn);
            }

            // Single character symbols
            ReadChar();
            switch (c)
            {
                case '=': return new Token(TokenType.Equal, "=", startLine, startColumn);
                case '+': return new Token(TokenType.Plus, "+", startLine, startColumn);
                case '-': return new Token(TokenType.Minus, "-", startLine, startColumn);
                case '*': return new Token(TokenType.Star, "*", startLine, startColumn);
                case '/': return new Token(TokenType.Divide, "/", startLine, startColumn);
                case '(': return new Token(TokenType.OpenParenthesis, "(", startLine, startColumn);
                case ')': return new Token(TokenType.CloseParenthesis, ")", startLine, startColumn);
                case ',': return new Token(TokenType.Comma, ",", startLine, startColumn);
                case '.': return new Token(TokenType.Dot, ".", startLine, startColumn);
                default:
                    return new Token(TokenType.Invalid, c.ToString(), startLine, startColumn);
            }
        }

        private char PeekAhead(int offset)
        {
            int index = _position + offset;
            return index >= _input.Length ? '\0' : _input[index];
        }

        private void SkipWhitespaceAndComments()
        {
            while (true)
            {
                char c = Peek();
                if (char.IsWhiteSpace(c))
                {
                    ReadChar();
                }
                else if (c == '-' && PeekAhead(1) == '-')
                {
                    // Single-line comment
                    ReadChar(); ReadChar();
                    while (Peek() != '\n' && Peek() != '\0')
                    {
                        ReadChar();
                    }
                }
                else if (c == '/' && PeekAhead(1) == '*')
                {
                    // Multi-line comment
                    ReadChar(); ReadChar();
                    while (true)
                    {
                        char nc = Peek();
                        if (nc == '\0') break;
                        if (nc == '*' && PeekAhead(1) == '/')
                        {
                            ReadChar(); ReadChar();
                            break;
                        }
                        ReadChar();
                    }
                }
                else
                {
                    break;
                }
            }
        }
    }
}
