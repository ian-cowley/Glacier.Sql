namespace Glacier.Sql.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;
using Xunit;
using ExecutionContext = Glacier.Sql.Engine.ExecutionContext;

public class ChallengerSqlStressTests : IDisposable
{
    private readonly string _testDir;
    private readonly CatalogManager _catalog;
    private readonly SqlEngine _engine;
    private readonly ExecutionContext _context;

    public ChallengerSqlStressTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"glacier_sql_challenge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _catalog = new CatalogManager(_testDir);
        _engine = new SqlEngine(_catalog);
        _context = new ExecutionContext(_catalog);
    }

    public void Dispose()
    {
        _catalog?.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task StressTest_Complex3WayJoinAndAggregations_ExecutesAccurately()
    {
        // Setup schema: customers, orders, order_items
        await _engine.ExecuteAsync("CREATE TABLE customers (id INT, name VARCHAR, region VARCHAR)", _context);
        await _engine.ExecuteAsync("CREATE TABLE orders (order_id INT, customer_id INT, status VARCHAR)", _context);
        await _engine.ExecuteAsync("CREATE TABLE order_items (item_id INT, order_id INT, price DOUBLE, quantity INT)", _context);

        // Populate data
        await _engine.ExecuteAsync("INSERT INTO customers VALUES (1, 'Acme Corp', 'North')", _context);
        await _engine.ExecuteAsync("INSERT INTO customers VALUES (2, 'Beta LLC', 'South')", _context);
        await _engine.ExecuteAsync("INSERT INTO customers VALUES (3, 'Gamma Inc', 'North')", _context);

        await _engine.ExecuteAsync("INSERT INTO orders VALUES (101, 1, 'Completed')", _context);
        await _engine.ExecuteAsync("INSERT INTO orders VALUES (102, 1, 'Pending')", _context);
        await _engine.ExecuteAsync("INSERT INTO orders VALUES (103, 2, 'Completed')", _context);
        await _engine.ExecuteAsync("INSERT INTO orders VALUES (104, 3, 'Completed')", _context);

        await _engine.ExecuteAsync("INSERT INTO order_items VALUES (1001, 101, 50.0, 2)", _context);
        await _engine.ExecuteAsync("INSERT INTO order_items VALUES (1002, 101, 25.0, 4)", _context);
        await _engine.ExecuteAsync("INSERT INTO order_items VALUES (1003, 102, 10.0, 1)", _context);
        await _engine.ExecuteAsync("INSERT INTO order_items VALUES (1004, 103, 200.0, 1)", _context);
        await _engine.ExecuteAsync("INSERT INTO order_items VALUES (1005, 104, 75.0, 3)", _context);

        // 3-way JOIN query ordering by projected column 'price'
        string joinSql = @"
            SELECT c.name, o.order_id, oi.price, oi.quantity
            FROM customers c
            JOIN orders o ON c.id = o.customer_id
            JOIN order_items oi ON o.order_id = oi.order_id
            ORDER BY oi.price ASC";

        var joinRes = await _engine.ExecuteAsync(joinSql, _context);
        Assert.True(joinRes.Success, joinRes.Message);
        Assert.NotNull(joinRes.DataFrame);
        Assert.Equal(5, joinRes.DataFrame.RowCount);

        // Verify ordering: lowest price first (10.0)
        Assert.Equal(10.0, joinRes.DataFrame.GetColumn("price").Get(0));

        // Aggregation query with grouping
        string aggSql = @"
            SELECT region, COUNT(id) AS cust_count
            FROM customers
            GROUP BY region";

        var aggRes = await _engine.ExecuteAsync(aggSql, _context);
        Assert.True(aggRes.Success, aggRes.Message);
        Assert.NotNull(aggRes.DataFrame);
        Assert.Equal(2, aggRes.DataFrame.RowCount);
    }

    [Fact]
    public async Task StressTest_SubqueriesExistsAndInList_ExecutesCorrectly()
    {
        await _engine.ExecuteAsync("CREATE TABLE depts (dept_id INT, dept_name VARCHAR)", _context);
        await _engine.ExecuteAsync("CREATE TABLE emps (emp_id INT, dept_id INT, name VARCHAR, salary DOUBLE)", _context);

        await _engine.ExecuteAsync("INSERT INTO depts VALUES (10, 'Engineering')", _context);
        await _engine.ExecuteAsync("INSERT INTO depts VALUES (20, 'Sales')", _context);
        await _engine.ExecuteAsync("INSERT INTO depts VALUES (30, 'Marketing')", _context); // Empty dept

        await _engine.ExecuteAsync("INSERT INTO emps VALUES (1, 10, 'Alice', 120000.0)", _context);
        await _engine.ExecuteAsync("INSERT INTO emps VALUES (2, 10, 'Bob', 110000.0)", _context);
        await _engine.ExecuteAsync("INSERT INTO emps VALUES (3, 20, 'Charlie', 95000.0)", _context);

        // Correlated EXISTS: find depts with employees, ordering by projected column dept_name
        string existsSql = @"
            SELECT d.dept_name
            FROM depts d
            WHERE EXISTS (SELECT 1 FROM emps e WHERE e.dept_id = d.dept_id)
            ORDER BY d.dept_name ASC";

        var existsRes = await _engine.ExecuteAsync(existsSql, _context);
        Assert.True(existsRes.Success, existsRes.Message);
        Assert.NotNull(existsRes.DataFrame);
        Assert.Equal(2, existsRes.DataFrame.RowCount);
        Assert.Equal("Engineering", existsRes.DataFrame.GetColumn("dept_name").Get(0));
        Assert.Equal("Sales", existsRes.DataFrame.GetColumn("dept_name").Get(1));

        // Correlated NOT EXISTS: find depts with NO employees
        string notExistsSql = @"
            SELECT d.dept_name
            FROM depts d
            WHERE NOT EXISTS (SELECT 1 FROM emps e WHERE e.dept_id = d.dept_id)";

        var notExistsRes = await _engine.ExecuteAsync(notExistsSql, _context);
        Assert.True(notExistsRes.Success, notExistsRes.Message);
        Assert.NotNull(notExistsRes.DataFrame);
        Assert.Equal(1, notExistsRes.DataFrame.RowCount);
        Assert.Equal("Marketing", notExistsRes.DataFrame.GetColumn("dept_name").Get(0));

        // IN subquery
        string inSubquerySql = @"
            SELECT e.name
            FROM emps e
            WHERE dept_id IN (SELECT dept_id FROM depts WHERE dept_name = 'Engineering')
            ORDER BY e.name ASC";

        var inRes = await _engine.ExecuteAsync(inSubquerySql, _context);
        Assert.True(inRes.Success, inRes.Message);
        Assert.NotNull(inRes.DataFrame);
        Assert.Equal(2, inRes.DataFrame.RowCount);
        Assert.Equal("Alice", inRes.DataFrame.GetColumn("name").Get(0));
        Assert.Equal("Bob", inRes.DataFrame.GetColumn("name").Get(1));

        // IN List literal expression
        string inListSql = "SELECT * FROM emps WHERE dept_id IN (20, 30)";
        var inListRes = await _engine.ExecuteAsync(inListSql, _context);
        Assert.True(inListRes.Success, inListRes.Message);
        Assert.Equal(1, inListRes.DataFrame!.RowCount);
        Assert.Equal("Charlie", inListRes.DataFrame.GetColumn("name").Get(0));
    }

    [Fact]
    public async Task StressTest_DdlDmlLifecycleAndTransactions_RollbackPreservesIntegrity()
    {
        await _engine.ExecuteAsync("CREATE TABLE ledger (id INT, val DOUBLE)", _context);
        await _engine.ExecuteAsync("INSERT INTO ledger VALUES (1, 100.0)", _context);
        await _engine.ExecuteAsync("INSERT INTO ledger VALUES (2, 200.0)", _context);

        // Transaction Rollback test
        await _engine.ExecuteAsync("BEGIN TRANSACTION", _context);
        await _engine.ExecuteAsync("INSERT INTO ledger VALUES (3, 300.0)", _context);
        await _engine.ExecuteAsync("UPDATE ledger SET val = 999.0 WHERE id = 1", _context);
        await _engine.ExecuteAsync("DELETE FROM ledger WHERE id = 2", _context);

        // Verify inside transaction changes took effect
        var midTxRes = await _engine.ExecuteAsync("SELECT * FROM ledger WHERE id = 3", _context);
        Assert.True(midTxRes.Success);
        Assert.Equal(1, midTxRes.DataFrame!.RowCount);

        // Rollback
        await _engine.ExecuteAsync("ROLLBACK", _context);

        // Verify state is completely reverted
        var postRollbackRes = await _engine.ExecuteAsync("SELECT * FROM ledger ORDER BY id ASC", _context);
        Assert.True(postRollbackRes.Success);
        Assert.NotNull(postRollbackRes.DataFrame);
        Assert.Equal(2, postRollbackRes.DataFrame.RowCount);
        Assert.Equal(100.0, postRollbackRes.DataFrame.GetColumn("val").Get(0));
        Assert.Equal(200.0, postRollbackRes.DataFrame.GetColumn("val").Get(1));

        // INSERT INTO ... SELECT
        await _engine.ExecuteAsync("CREATE TABLE ledger_copy (id INT, val DOUBLE)", _context);
        await _engine.ExecuteAsync("INSERT INTO ledger_copy SELECT id, val FROM ledger", _context);

        var copyRes = await _engine.ExecuteAsync("SELECT * FROM ledger_copy ORDER BY id ASC", _context);
        Assert.True(copyRes.Success);
        Assert.Equal(2, copyRes.DataFrame!.RowCount);

        // Views DDL
        await _engine.ExecuteAsync("CREATE VIEW v_ledger AS SELECT id, val FROM ledger WHERE val > 150.0", _context);
        var viewRes = await _engine.ExecuteAsync("SELECT * FROM v_ledger", _context);
        Assert.True(viewRes.Success);
        Assert.Equal(1, viewRes.DataFrame!.RowCount);
        Assert.Equal(2, viewRes.DataFrame.GetColumn("id").Get(0));

        await _engine.ExecuteAsync("DROP VIEW v_ledger", _context);
        Assert.False(_catalog.ViewExists("v_ledger"));

        // DROP TABLE
        var dropRes = await _engine.ExecuteAsync("DROP TABLE ledger_copy", _context);
        Assert.True(dropRes.Success);
        Assert.False(_catalog.TableExists("ledger_copy"));
    }

    [Fact]
    public async Task StressTest_BuiltInFunctionsAndCalculations()
    {
        await _engine.ExecuteAsync("CREATE TABLE metrics (id INT, val DOUBLE, tag VARCHAR)", _context);
        var ins1 = await _engine.ExecuteAsync("INSERT INTO metrics VALUES (1, 16.0, 'low')", _context);
        Assert.True(ins1.Success);
        var ins2 = await _engine.ExecuteAsync("INSERT INTO metrics VALUES (2, 25.0, 'high')", _context);
        Assert.True(ins2.Success);

        // Built-in functions: ABS, UPPER, SQRT, ROUND, LEN
        string fnSql = "SELECT id, ABS(val) AS abs_val, SQRT(val) AS sqrt_val, UPPER(tag) AS upper_tag, LEN(tag) AS tag_len FROM metrics WHERE id = 1";
        var fnRes = await _engine.ExecuteAsync(fnSql, _context);
        Assert.True(fnRes.Success, fnRes.Message);
        Assert.NotNull(fnRes.DataFrame);
        Assert.Equal(1, fnRes.DataFrame.RowCount);
        Assert.Equal(16.0, Convert.ToDouble(fnRes.DataFrame.GetColumn("abs_val").Get(0)));
        Assert.Equal(4.0, Convert.ToDouble(fnRes.DataFrame.GetColumn("sqrt_val").Get(0)));
        Assert.Equal("LOW", fnRes.DataFrame.GetColumn("upper_tag").Get(0));
        Assert.Equal(3, Convert.ToInt32(fnRes.DataFrame.GetColumn("tag_len").Get(0)));
    }

    [Fact]
    public async Task VulnerabilityFinding_NegativeNumberInInsert_ThrowsNotSupportedException()
    {
        await _engine.ExecuteAsync("CREATE TABLE num_table (val DOUBLE)", _context);
        
        // Negative number literal in INSERT is parsed as SqlUnaryExpression("-", SqlLiteral(42.0)),
        // which SqlEngine.Dml line 42 rejects because it only permits SqlLiteral.
        var res = await _engine.ExecuteAsync("INSERT INTO num_table VALUES (-42.0)", _context);
        Assert.False(res.Success);
        Assert.Contains("Only literal values are supported in INSERT statements", res.Message);
    }

    [Fact]
    public async Task VulnerabilityFinding_AlterTableAddVarchar_FailsDueToNullStringArray()
    {
        await _engine.ExecuteAsync("CREATE TABLE employee_notes (id INT)", _context);
        await _engine.ExecuteAsync("INSERT INTO employee_notes VALUES (1)", _context);

        // When ALTER TABLE ADD ... VARCHAR executes on a table with rows, SqlEngine.CreateNullSeries creates
        // new string[length] which has null elements. Utf8StringSeries constructor does not accept null elements,
        // throwing ArgumentNullException: Value cannot be null. (Parameter 'chars').
        var alterRes = await _engine.ExecuteAsync("ALTER TABLE employee_notes ADD notes VARCHAR", _context);
        Assert.False(alterRes.Success);
        Assert.Contains("ArgumentNullException", alterRes.Message);
    }

    [Fact]
    public async Task StressTest_AdversarialSyntaxAndEdgeCases_FailsCleanlyWithoutCrash()
    {
        // 1. Invalid syntax
        var syntaxErr = await _engine.ExecuteAsync("SELECT FROM WHERE 123", _context);
        Assert.False(syntaxErr.Success);
        Assert.NotNull(syntaxErr.Message);

        // 2. Non-existent table
        var noTableErr = await _engine.ExecuteAsync("SELECT * FROM non_existent_table_xyz", _context);
        Assert.False(noTableErr.Success);
        Assert.Contains("non_existent_table_xyz", noTableErr.Message, StringComparison.OrdinalIgnoreCase);

        // 3. Parser stress on deep expressions
        string deepExpr = "SELECT " + string.Join(" + ", System.Linq.Enumerable.Repeat("1", 50)) + " AS sum_50";
        var deepRes = await _engine.ExecuteAsync(deepExpr, _context);
        Assert.True(deepRes.Success);
        Assert.Equal(50, Convert.ToInt32(deepRes.DataFrame!.GetColumn("sum_50").Get(0)));

        // 4. Bracketed identifiers and string escapes
        await _engine.ExecuteAsync("CREATE TABLE [t with spaces] ([col 1] INT, [col 2] VARCHAR)", _context);
        var insertEscaped = await _engine.ExecuteAsync("INSERT INTO [t with spaces] VALUES (42, 'Hello ''World''')", _context);
        Assert.True(insertEscaped.Success);

        var selectEscaped = await _engine.ExecuteAsync("SELECT [col 1], [col 2] FROM [t with spaces]", _context);
        Assert.True(selectEscaped.Success);
        Assert.Equal(42, selectEscaped.DataFrame!.GetColumn("col 1").Get(0));
        Assert.Equal("Hello 'World'", selectEscaped.DataFrame.GetColumn("col 2").Get(0));

        // 5. Unprojected column in ORDER BY throws execution error (capturing known engine constraint)
        var unprojectedErr = await _engine.ExecuteAsync("SELECT [col 1] FROM [t with spaces] ORDER BY [col 2] ASC", _context);
        Assert.False(unprojectedErr.Success);
        Assert.Contains("Execution Error", unprojectedErr.Message);
    }
}
