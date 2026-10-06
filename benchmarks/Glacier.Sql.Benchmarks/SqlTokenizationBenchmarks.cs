namespace Glacier.Sql.Benchmarks;

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Glacier.Sql.Parser;

[MemoryDiagnoser]
public class SqlTokenizationBenchmarks
{
    private const string SimpleSelectSql = 
        "SELECT id, name, salary FROM Employees WHERE salary > 50000;";

    private const string ComplexJoinSql = 
        "SELECT e.id, e.name, d.dept_name, SUM(s.amount) FROM Employees e " +
        "INNER JOIN Departments d ON e.dept_id = d.id " +
        "LEFT JOIN Sales s ON e.id = s.emp_id " +
        "WHERE e.status = 'ACTIVE' " +
        "GROUP BY e.id, e.name, d.dept_name " +
        "ORDER BY e.name ASC;";

    private const string InsertBatchSql = 
        "INSERT INTO Customers (id, name, balance, active) VALUES (101, 'Jane Doe', 1520.50, 1);";

    private const string DdlCreateTableSql = 
        "CREATE TABLE Orders (id INT PRIMARY KEY, customer_id INT NOT NULL, order_date DATETIME, total FLOAT CHECK (total >= 0));";

    [Benchmark(Baseline = true)]
    public List<Token> TokenizeSimpleSelect()
    {
        var lexer = new SqlLexer(SimpleSelectSql);
        return lexer.Tokenize();
    }

    [Benchmark]
    public List<Token> TokenizeComplexJoin()
    {
        var lexer = new SqlLexer(ComplexJoinSql);
        return lexer.Tokenize();
    }

    [Benchmark]
    public List<Token> TokenizeInsertBatch()
    {
        var lexer = new SqlLexer(InsertBatchSql);
        return lexer.Tokenize();
    }

    [Benchmark]
    public List<Token> TokenizeDdlCreateTable()
    {
        var lexer = new SqlLexer(DdlCreateTableSql);
        return lexer.Tokenize();
    }
}
