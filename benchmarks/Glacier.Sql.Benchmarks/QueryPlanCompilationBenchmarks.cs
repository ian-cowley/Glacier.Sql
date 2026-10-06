namespace Glacier.Sql.Benchmarks;

using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;

[MemoryDiagnoser]
public class QueryPlanCompilationBenchmarks
{
    private string _tempDir = null!;
    private CatalogManager _catalog = null!;
    private QueryPlanner _planner = null!;

    private SelectStatement _simpleSelectAst = null!;
    private SelectStatement _complexJoinAst = null!;
    private SelectStatement _aggregateGroupByAst = null!;
    private SqlExpression _scalarExpr = null!;
    private SqlExpression _predicateExpr = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GlacierSqlBdn_" + Guid.NewGuid().ToString("N"));
        _catalog = new CatalogManager(_tempDir);
        _planner = new QueryPlanner(_catalog);

        // Setup schemas
        var empCols = new List<ColumnMetadata>
        {
            new() { Name = "id", DataType = "INT", IsPrimaryKey = true, IsNullable = false },
            new() { Name = "name", DataType = "VARCHAR", IsNullable = false },
            new() { Name = "salary", DataType = "FLOAT", IsNullable = false },
            new() { Name = "dept_id", DataType = "INT", IsNullable = false },
            new() { Name = "status", DataType = "VARCHAR", IsNullable = false }
        };
        _catalog.AddTable("Employees", empCols);
        TableStorage.InitializeTable(_catalog.GetTable("Employees")!.BackingFile, empCols);

        var deptCols = new List<ColumnMetadata>
        {
            new() { Name = "id", DataType = "INT", IsPrimaryKey = true, IsNullable = false },
            new() { Name = "dept_name", DataType = "VARCHAR", IsNullable = false }
        };
        _catalog.AddTable("Departments", deptCols);
        TableStorage.InitializeTable(_catalog.GetTable("Departments")!.BackingFile, deptCols);

        var salesCols = new List<ColumnMetadata>
        {
            new() { Name = "id", DataType = "INT", IsPrimaryKey = true, IsNullable = false },
            new() { Name = "emp_id", DataType = "INT", IsNullable = false },
            new() { Name = "amount", DataType = "FLOAT", IsNullable = false }
        };
        _catalog.AddTable("Sales", salesCols);
        TableStorage.InitializeTable(_catalog.GetTable("Sales")!.BackingFile, salesCols);

        // Pre-parse ASTs
        const string simpleSelect = "SELECT id, name, salary FROM Employees WHERE salary > 50000;";
        _simpleSelectAst = (SelectStatement)new TSqlParser(new SqlLexer(simpleSelect).Tokenize()).Parse();

        const string complexJoin = 
            "SELECT e.id, e.name, d.dept_name FROM Employees e " +
            "INNER JOIN Departments d ON e.dept_id = d.id " +
            "WHERE e.salary > 50000;";
        _complexJoinAst = (SelectStatement)new TSqlParser(new SqlLexer(complexJoin).Tokenize()).Parse();

        const string aggregateGroupBy = 
            "SELECT dept_id, SUM(salary) AS total_sal, AVG(salary) AS avg_sal, COUNT(id) AS emp_count " +
            "FROM Employees GROUP BY dept_id;";
        _aggregateGroupByAst = (SelectStatement)new TSqlParser(new SqlLexer(aggregateGroupBy).Tokenize()).Parse();

        // Pre-parse expressions
        _scalarExpr = new TSqlParser(new SqlLexer("(salary * 1.1) + 500.0").Tokenize()).ParseExpression(0);
        _predicateExpr = new TSqlParser(new SqlLexer("salary > 50000 AND status = 'ACTIVE'").Tokenize()).ParseExpression(0);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Benchmark(Baseline = true)]
    public LazyFrame PlanSimpleSelect()
    {
        return _planner.PlanQuery(_simpleSelectAst);
    }

    [Benchmark]
    public LazyFrame PlanComplexJoin()
    {
        return _planner.PlanQuery(_complexJoinAst);
    }

    [Benchmark]
    public LazyFrame PlanAggregateGroupBy()
    {
        return _planner.PlanQuery(_aggregateGroupByAst);
    }

    [Benchmark]
    public Expr CompileScalarExpression()
    {
        return _planner.CompileExpression(_scalarExpr, "Employees");
    }

    [Benchmark]
    public Expr CompilePredicateExpression()
    {
        return _planner.CompileExpression(_predicateExpr, "Employees");
    }
}
