using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Glacier.Sql.Parser
{
    public enum TokenType
    {
        // Keywords
        Select, From, Join, Inner, Left, Right, Full, Outer, Cross, On, Where, Group, By, Having, Order, Asc, Desc, Top, As, And, Or, Not, Is, Null, Create, Table, Drop, Insert, Into, Values, Update, Set, Delete, Begin, Transaction, Commit, Rollback,
        Trigger, After, Instead, Of,
        Alter, Add, Column, View, Unique, Check, Primary, Key, In, Exists, Constraint,

        // Data Types
        Int, Float, Varchar, Bit, Datetime,

        // Identifiers & Literals
        Identifier, StringLiteral, NumberLiteral, BooleanLiteral,

        // Symbols & Operators
        Equal, NotEqual, GreaterThan, LessThan, GreaterOrEqual, LessOrEqual,
        Plus, Minus, Multiply, Divide,
        OpenParenthesis, CloseParenthesis, Comma, Dot, Star, Semicolon,

        // Special
        EOF, Invalid
    }

    public record Token(TokenType Type, string Text, int Line, int Column);

    public class SqlLexer
    {
        private sealed class KeywordTrieNode
        {
            public readonly KeywordTrieNode?[] Children = new KeywordTrieNode?[128];
            public bool IsTerminal;
            public TokenType TokenType;
            public string? CanonicalText;
        }

        private static readonly KeywordTrieNode KeywordTrieRoot = BuildKeywordTrie();

        private static KeywordTrieNode BuildKeywordTrie()
        {
            var root = new KeywordTrieNode();

            void Insert(string keyword, TokenType tokenType)
            {
                KeywordTrieNode current = root;
                for (int i = 0; i < keyword.Length; i++)
                {
                    byte b = ToUpperAscii((byte)keyword[i]);
                    var next = current.Children[b];
                    if (next == null)
                    {
                        next = new KeywordTrieNode();
                        current.Children[b] = next;
                    }
                    current = next;
                }
                current.IsTerminal = true;
                current.TokenType = tokenType;
                current.CanonicalText = keyword;
            }

            Insert("SELECT", TokenType.Select);
            Insert("FROM", TokenType.From);
            Insert("JOIN", TokenType.Join);
            Insert("INNER", TokenType.Inner);
            Insert("LEFT", TokenType.Left);
            Insert("RIGHT", TokenType.Right);
            Insert("FULL", TokenType.Full);
            Insert("OUTER", TokenType.Outer);
            Insert("CROSS", TokenType.Cross);
            Insert("ON", TokenType.On);
            Insert("WHERE", TokenType.Where);
            Insert("GROUP", TokenType.Group);
            Insert("BY", TokenType.By);
            Insert("HAVING", TokenType.Having);
            Insert("ORDER", TokenType.Order);
            Insert("ASC", TokenType.Asc);
            Insert("DESC", TokenType.Desc);
            Insert("TOP", TokenType.Top);
            Insert("AS", TokenType.As);
            Insert("AND", TokenType.And);
            Insert("OR", TokenType.Or);
            Insert("NOT", TokenType.Not);
            Insert("IS", TokenType.Is);
            Insert("NULL", TokenType.Null);
            Insert("CREATE", TokenType.Create);
            Insert("TABLE", TokenType.Table);
            Insert("DROP", TokenType.Drop);
            Insert("INSERT", TokenType.Insert);
            Insert("INTO", TokenType.Into);
            Insert("VALUES", TokenType.Values);
            Insert("UPDATE", TokenType.Update);
            Insert("SET", TokenType.Set);
            Insert("DELETE", TokenType.Delete);
            Insert("BEGIN", TokenType.Begin);
            Insert("TRANSACTION", TokenType.Transaction);
            Insert("TRAN", TokenType.Transaction);
            Insert("COMMIT", TokenType.Commit);
            Insert("ROLLBACK", TokenType.Rollback);
            Insert("TRIGGER", TokenType.Trigger);
            Insert("AFTER", TokenType.After);
            Insert("INSTEAD", TokenType.Instead);
            Insert("OF", TokenType.Of);
            Insert("ALTER", TokenType.Alter);
            Insert("ADD", TokenType.Add);
            Insert("COLUMN", TokenType.Column);
            Insert("VIEW", TokenType.View);
            Insert("UNIQUE", TokenType.Unique);
            Insert("CHECK", TokenType.Check);
            Insert("PRIMARY", TokenType.Primary);
            Insert("KEY", TokenType.Key);
            Insert("IN", TokenType.In);
            Insert("EXISTS", TokenType.Exists);
            Insert("CONSTRAINT", TokenType.Constraint);

            // Types
            Insert("INT", TokenType.Int);
            Insert("INTEGER", TokenType.Int);
            Insert("FLOAT", TokenType.Float);
            Insert("REAL", TokenType.Float);
            Insert("DOUBLE", TokenType.Float);
            Insert("VARCHAR", TokenType.Varchar);
            Insert("NVARCHAR", TokenType.Varchar);
            Insert("TEXT", TokenType.Varchar);
            Insert("BIT", TokenType.Bit);
            Insert("BOOLEAN", TokenType.Bit);
            Insert("DATETIME", TokenType.Datetime);
            Insert("DATE", TokenType.Datetime);

            // Boolean values
            Insert("TRUE", TokenType.BooleanLiteral);
            Insert("FALSE", TokenType.BooleanLiteral);

            return root;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ToUpperAscii(byte b)
        {
            if (b >= 97 && b <= 122)
            {
                return (byte)(b - 32);
            }
            return b;
        }

        private static bool TryMatchKeyword(ReadOnlySpan<char> span, out TokenType tokenType, out string canonicalText)
        {
            KeywordTrieNode current = KeywordTrieRoot;
            for (int i = 0; i < span.Length; i++)
            {
                char ch = span[i];
                if (ch >= 128)
                {
                    tokenType = default;
                    canonicalText = string.Empty;
                    return false;
                }

                byte upper = ToUpperAscii((byte)ch);
                var next = current.Children[upper];
                if (next == null)
                {
                    tokenType = default;
                    canonicalText = string.Empty;
                    return false;
                }
                current = next;
            }

            if (current.IsTerminal)
            {
                tokenType = current.TokenType;
                canonicalText = current.CanonicalText!;
                return true;
            }

            tokenType = default;
            canonicalText = string.Empty;
            return false;
        }

        public static bool IsKeyword(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return TryMatchKeyword(text.AsSpan(), out _, out _);
        }

        private readonly string _input;
        private int _position;
        private int _line = 1;
        private int _column = 1;

        public SqlLexer(string input)
        {
            _input = input ?? "";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private char Peek() => _position >= _input.Length ? '\0' : _input[_position];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private char PeekAhead(int offset)
        {
            int index = _position + offset;
            return (uint)index >= (uint)_input.Length ? '\0' : _input[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            int estimatedCapacity = Math.Max(8, _input.Length / 5);
            var tokens = new List<Token>(estimatedCapacity);
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

            ReadOnlySpan<char> inputSpan = _input.AsSpan();
            char c = inputSpan[_position];

            // Identifiers with brackets [Column Name]
            if (c == '[')
            {
                ReadChar(); // consume '['
                int contentStart = _position;
                while (_position < inputSpan.Length && inputSpan[_position] != ']')
                {
                    ReadChar();
                }
                int contentLength = _position - contentStart;
                string ident = contentLength == 0 ? string.Empty : _input.Substring(contentStart, contentLength);
                if (Peek() == ']')
                {
                    ReadChar(); // consume ']'
                }
                return new Token(TokenType.Identifier, ident, startLine, startColumn);
            }

            // String literals 'String Value'
            if (c == '\'')
            {
                ReadChar(); // consume opening quote
                int contentStart = _position;
                bool hasEscape = false;

                while (_position < inputSpan.Length)
                {
                    char next = inputSpan[_position];
                    if (next == '\'')
                    {
                        ReadChar(); // consume quote
                        if (Peek() == '\'')
                        {
                            hasEscape = true;
                            ReadChar(); // consume escaped quote
                        }
                        else
                        {
                            // End of string (excluding the closing quote we just consumed)
                            int rawLength = (_position - 1) - contentStart;
                            if (!hasEscape)
                            {
                                string lit = rawLength == 0 ? string.Empty : _input.Substring(contentStart, rawLength);
                                return new Token(TokenType.StringLiteral, lit, startLine, startColumn);
                            }

                            string unescaped = UnescapeQuotedString(inputSpan.Slice(contentStart, rawLength));
                            return new Token(TokenType.StringLiteral, unescaped, startLine, startColumn);
                        }
                    }
                    else
                    {
                        ReadChar();
                    }
                }

                // Unterminated string literal
                int unterminatedLen = _position - contentStart;
                string unterminated = !hasEscape
                    ? (unterminatedLen == 0 ? string.Empty : _input.Substring(contentStart, unterminatedLen))
                    : UnescapeQuotedString(inputSpan.Slice(contentStart, unterminatedLen));
                return new Token(TokenType.StringLiteral, unterminated, startLine, startColumn);
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && char.IsDigit(PeekAhead(1))))
            {
                int start = _position;
                bool hasDot = false;
                while (_position < inputSpan.Length)
                {
                    char nc = inputSpan[_position];
                    if (char.IsDigit(nc))
                    {
                        ReadChar();
                    }
                    else if (nc == '.' && !hasDot)
                    {
                        hasDot = true;
                        ReadChar();
                    }
                    else
                    {
                        break;
                    }
                }
                return new Token(TokenType.NumberLiteral, _input.Substring(start, _position - start), startLine, startColumn);
            }

            // Identifiers / Keywords
            if (char.IsLetter(c) || c == '_')
            {
                int start = _position;
                ReadChar();
                while (_position < inputSpan.Length)
                {
                    char nc = inputSpan[_position];
                    if (char.IsLetterOrDigit(nc) || nc == '_')
                    {
                        ReadChar();
                    }
                    else
                    {
                        break;
                    }
                }

                int length = _position - start;
                ReadOnlySpan<char> wordSpan = inputSpan.Slice(start, length);
                if (TryMatchKeyword(wordSpan, out var kwType, out var canonical))
                {
                    return new Token(kwType, canonical, startLine, startColumn);
                }
                return new Token(TokenType.Identifier, _input.Substring(start, length), startLine, startColumn);
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
                case ';': return new Token(TokenType.Semicolon, ";", startLine, startColumn);
                default:
                    return new Token(TokenType.Invalid, c.ToString(), startLine, startColumn);
            }
        }

        private static string UnescapeQuotedString(ReadOnlySpan<char> raw)
        {
            char[] rented = ArrayPool<char>.Shared.Rent(raw.Length);
            try
            {
                int outIdx = 0;
                for (int i = 0; i < raw.Length; i++)
                {
                    char ch = raw[i];
                    rented[outIdx++] = ch;
                    if (ch == '\'' && i + 1 < raw.Length && raw[i + 1] == '\'')
                    {
                        i++;
                    }
                }
                return new string(rented, 0, outIdx);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
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
