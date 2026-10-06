namespace Glacier.Sql.Benchmarks;

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Glacier.Sql.Parser;

[MemoryDiagnoser]
public class SqlAstGenerationBenchmarks
{
    private List<Token> _simpleSelectTokens = null!;
    private List<Token> _complexJoinTokens = null!;
    private List<Token> _insertBatchTokens = null!;
    private List<Token> _ddlCreateTableTokens = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string simpleSelect = 
            "SELECT id, name, salary FROM Employees WHERE salary > 50000;";

        const string complexJoin = 
            "SELECT e.id, e.name, d.dept_name, SUM(s.amount) FROM Employees e " +
            "INNER JOIN Departments d ON e.dept_id = d.id " +
            "LEFT JOIN Sales s ON e.id = s.emp_id " +
            "WHERE e.status = 'ACTIVE' " +
            "GROUP BY e.id, e.name, d.dept_name " +
            "ORDER BY e.name ASC;";

        const string insertBatch = 
            "INSERT INTO Customers (id, name, balance, active) VALUES (101, 'Jane Doe', 1520.50, 1);";

        const string ddlCreateTable = 
            "CREATE TABLE Orders (id INT PRIMARY KEY, customer_id INT NOT NULL, order_date DATETIME, total FLOAT CHECK (total >= 0));";

        _simpleSelectTokens = new SqlLexer(simpleSelect).Tokenize();
        _complexJoinTokens = new SqlLexer(complexJoin).Tokenize();
        _insertBatchTokens = new SqlLexer(insertBatch).Tokenize();
        _ddlCreateTableTokens = new SqlLexer(ddlCreateTable).Tokenize();
    }

    [Benchmark(Baseline = true)]
    public SqlStatement ParseSimpleSelectAst()
    {
        var parser = new TSqlParser(_simpleSelectTokens);
        return parser.Parse();
    }

    [Benchmark]
    public SqlStatement ParseComplexJoinAst()
    {
        var parser = new TSqlParser(_complexJoinTokens);
        return parser.Parse();
    }

    [Benchmark]
    public SqlStatement ParseInsertBatchAst()
    {
        var parser = new TSqlParser(_insertBatchTokens);
        return parser.Parse();
    }

    [Benchmark]
    public SqlStatement ParseDdlCreateTableAst()
    {
        var parser = new TSqlParser(_ddlCreateTableTokens);
        return parser.Parse();
    }
}
