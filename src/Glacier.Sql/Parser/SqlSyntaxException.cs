using System;

namespace Glacier.Sql.Parser
{
    public class SqlSyntaxException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public SqlSyntaxException(string message) : base(message)
        {
        }

        public SqlSyntaxException(string message, int line, int column) : base(message)
        {
            Line = line;
            Column = column;
        }

        public SqlSyntaxException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
