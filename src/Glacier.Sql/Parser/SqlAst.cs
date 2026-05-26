using System;
using System.Collections.Generic;

namespace Glacier.Sql.Parser
{
    public abstract class SqlNode { }

    public abstract class SqlStatement : SqlNode { }

    public class CreateTableStatement : SqlStatement
    {
        public string TableName { get; }
        public List<ColumnDefinition> Columns { get; }

        public CreateTableStatement(string tableName, List<ColumnDefinition> columns)
        {
            TableName = tableName;
            Columns = columns;
        }
    }

    public class ColumnDefinition : SqlNode
    {
        public string Name { get; }
        public string DataType { get; }
        public bool IsNullable { get; }
        public bool IsPrimaryKey { get; }
        public bool IsUnique { get; }
        public string? CheckExpression { get; }

        public ColumnDefinition(string name, string dataType, bool isNullable = true, bool isPrimaryKey = false, bool isUnique = false, string? checkExpression = null)
        {
            Name = name;
            DataType = dataType;
            IsNullable = isNullable;
            IsPrimaryKey = isPrimaryKey;
            IsUnique = isUnique;
            CheckExpression = checkExpression;
        }
    }

    public class DropTableStatement : SqlStatement
    {
        public string TableName { get; }

        public DropTableStatement(string tableName)
        {
            TableName = tableName;
        }
    }

    public class InsertStatement : SqlStatement
    {
        public string TableName { get; }
        public List<string>? Columns { get; }
        public List<SqlExpression> Values { get; }

        public InsertStatement(string tableName, List<string>? columns, List<SqlExpression> values)
        {
            TableName = tableName;
            Columns = columns;
            Values = values;
        }
    }

    public class SelectStatement : SqlStatement
    {
        public int? Top { get; set; }
        public List<SelectItem> Projections { get; } = new();
        public SqlTableRef? From { get; set; }
        public List<SqlJoin> Joins { get; } = new();
        public SqlExpression? Where { get; set; }
        public List<SqlExpression> GroupBy { get; } = new();
        public SqlExpression? Having { get; set; }
        public List<SqlOrderBy> OrderBy { get; } = new();

        public SelectStatement() { }
    }

    public class DeleteStatement : SqlStatement
    {
        public string TableName { get; }
        public SqlExpression? Where { get; }

        public DeleteStatement(string tableName, SqlExpression? where = null)
        {
            TableName = tableName;
            Where = where;
        }
    }

    public class UpdateAssignment : SqlNode
    {
        public string ColumnName { get; }
        public SqlExpression Expression { get; }

        public UpdateAssignment(string columnName, SqlExpression expression)
        {
            ColumnName = columnName;
            Expression = expression;
        }
    }

    public class UpdateStatement : SqlStatement
    {
        public string TableName { get; }
        public List<UpdateAssignment> Assignments { get; }
        public SqlExpression? Where { get; }

        public UpdateStatement(string tableName, List<UpdateAssignment> assignments, SqlExpression? where = null)
        {
            TableName = tableName;
            Assignments = assignments;
            Where = where;
        }
    }

    public class BeginTransactionStatement : SqlStatement { }
    public class CommitTransactionStatement : SqlStatement { }
    public class RollbackTransactionStatement : SqlStatement { }

    public class CreateTriggerStatement : SqlStatement
    {
        public string TriggerName { get; }
        public string TableName { get; }
        public string EventType { get; } // "INSERT", "UPDATE", "DELETE"
        public string Timing { get; } // "AFTER" or "INSTEAD OF"
        public string ActionSql { get; }

        public CreateTriggerStatement(string triggerName, string tableName, string eventType, string timing, string actionSql)
        {
            TriggerName = triggerName;
            TableName = tableName;
            EventType = eventType;
            Timing = timing;
            ActionSql = actionSql;
        }
    }

    public class InsertSelectStatement : SqlStatement
    {
        public string TableName { get; }
        public List<string>? Columns { get; }
        public SelectStatement SelectQuery { get; }

        public InsertSelectStatement(string tableName, List<string>? columns, SelectStatement selectQuery)
        {
            TableName = tableName;
            Columns = columns;
            SelectQuery = selectQuery;
        }
    }

    public abstract class SqlTableRef : SqlNode { }

    public class SqlTableSource : SqlTableRef
    {
        public string TableName { get; }
        public string? Alias { get; }

        public SqlTableSource(string tableName, string? alias = null)
        {
            TableName = tableName;
            Alias = alias;
        }
    }

    public class SqlJoin : SqlNode
    {
        public SqlTableRef Table { get; }
        public string JoinType { get; } // "INNER", "LEFT", "CROSS"
        public SqlExpression? On { get; set; }

        public SqlJoin(SqlTableRef table, string joinType, SqlExpression? on = null)
        {
            Table = table;
            JoinType = joinType;
            On = on;
        }
    }

    public class SelectItem : SqlNode
    {
        public SqlExpression Expression { get; }
        public string? Alias { get; }

        public SelectItem(SqlExpression expression, string? alias = null)
        {
            Expression = expression;
            Alias = alias;
        }
    }

    public class SqlOrderBy : SqlNode
    {
        public SqlExpression Expression { get; }
        public bool Descending { get; }

        public SqlOrderBy(SqlExpression expression, bool descending = false)
        {
            Expression = expression;
            Descending = descending;
        }
    }

    // Expressions
    public abstract class SqlExpression : SqlNode { }

    public class SqlColumnRef : SqlExpression
    {
        public string? Prefix { get; } // e.g. "t1" in "t1.col"
        public string ColumnName { get; }

        public SqlColumnRef(string columnName, string? prefix = null)
        {
            ColumnName = columnName;
            Prefix = prefix;
        }

        public override string ToString() => Prefix != null ? $"{Prefix}.{ColumnName}" : ColumnName;
    }

    public class SqlStarRef : SqlExpression
    {
        public string? Prefix { get; }

        public SqlStarRef(string? prefix = null)
        {
            Prefix = prefix;
        }

        public override string ToString() => Prefix != null ? $"{Prefix}.*" : "*";
    }

    public class SqlLiteral : SqlExpression
    {
        public object? Value { get; }

        public SqlLiteral(object? value)
        {
            Value = value;
        }

        public override string ToString() => Value?.ToString() ?? "NULL";
    }

    public class SqlBinaryExpression : SqlExpression
    {
        public SqlExpression Left { get; }
        public string Operator { get; } // "+", "-", "*", "/", "=", "<>", ">", "<", ">=", "<=", "AND", "OR", "IS", "IS NOT"
        public SqlExpression Right { get; }

        public SqlBinaryExpression(SqlExpression left, string op, SqlExpression right)
        {
            Left = left;
            Operator = op;
            Right = right;
        }

        public override string ToString() => $"({Left} {Operator} {Right})";
    }

    public class SqlUnaryExpression : SqlExpression
    {
        public string Operator { get; } // "NOT", "-"
        public SqlExpression Operand { get; }

        public SqlUnaryExpression(string op, SqlExpression operand)
        {
            Operator = op;
            Operand = operand;
        }

        public override string ToString() => $"({Operator} {Operand})";
    }

    public class SqlFunctionCall : SqlExpression
    {
        public string FunctionName { get; }
        public List<SqlExpression> Arguments { get; }

        public SqlFunctionCall(string functionName, List<SqlExpression> arguments)
        {
            FunctionName = functionName;
            Arguments = arguments;
        }

        public override string ToString() => $"{FunctionName}({string.Join(", ", Arguments)})";
    }

    public class AlterTableStatement : SqlStatement
    {
        public string TableName { get; }
        public string AlterAction { get; } // "ADD" or "DROP"
        public string? ColumnName { get; }
        public string? DataType { get; } // null if DROP
        public ColumnDefinition? ColumnDef { get; }

        public AlterTableStatement(string tableName, string alterAction, string? columnName, string? dataType, ColumnDefinition? columnDef = null)
        {
            TableName = tableName;
            AlterAction = alterAction;
            ColumnName = columnName;
            DataType = dataType;
            ColumnDef = columnDef;
        }
    }

    public class CreateViewStatement : SqlStatement
    {
        public string ViewName { get; }
        public SelectStatement SelectQuery { get; }
        public string DefinitionSql { get; }

        public CreateViewStatement(string viewName, SelectStatement selectQuery, string definitionSql)
        {
            ViewName = viewName;
            SelectQuery = selectQuery;
            DefinitionSql = definitionSql;
        }
    }

    public class DropViewStatement : SqlStatement
    {
        public string ViewName { get; }

        public DropViewStatement(string viewName)
        {
            ViewName = viewName;
        }
    }

    public class SqlSubqueryExpression : SqlExpression
    {
        public SelectStatement SelectQuery { get; }

        public SqlSubqueryExpression(SelectStatement selectQuery)
        {
            SelectQuery = selectQuery;
        }

        public override string ToString() => $"(SELECT ...)";
    }

    public class SqlExistsExpression : SqlExpression
    {
        public SelectStatement SelectQuery { get; }

        public SqlExistsExpression(SelectStatement selectQuery)
        {
            SelectQuery = selectQuery;
        }

        public override string ToString() => $"EXISTS (SELECT ...)";
    }

    public class SqlInSubqueryExpression : SqlExpression
    {
        public SqlExpression Left { get; }
        public SelectStatement Subquery { get; }

        public SqlInSubqueryExpression(SqlExpression left, SelectStatement subquery)
        {
            Left = left;
            Subquery = subquery;
        }

        public override string ToString() => $"{Left} IN (SELECT ...)";
    }

    public class SqlInListExpression : SqlExpression
    {
        public SqlExpression Left { get; }
        public List<SqlExpression> List { get; }

        public SqlInListExpression(SqlExpression left, List<SqlExpression> list)
        {
            Left = left;
            List = list;
        }

        public override string ToString() => $"{Left} IN ({string.Join(", ", List)})";
    }
}
