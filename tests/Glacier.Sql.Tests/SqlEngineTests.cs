using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;
using Xunit;
using Xunit.Abstractions;

namespace Glacier.Sql.Tests
{
    public class SqlEngineTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _tempDataDir;
        private readonly CatalogManager _catalog;
        private readonly SqlEngine _engine;
        private readonly Engine.ExecutionContext _context;

        public SqlEngineTests(ITestOutputHelper output)
        {
            _output = output;
            _tempDataDir = Path.Combine(Path.GetTempPath(), "GlacierSqlTest_" + Guid.NewGuid().ToString("N"));
            _catalog = new CatalogManager(_tempDataDir);
            _engine = new SqlEngine(_catalog);
            _context = new Engine.ExecutionContext(_catalog);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDataDir))
            {
                try { Directory.Delete(_tempDataDir, true); } catch { }
            }
        }

        [Fact]
        public void TestLexerAndParser()
        {
            string sql = "SELECT TOP 5 id, [name], UPPER(email) AS upper_email FROM [users] WHERE age >= 18 ORDER BY id DESC";
            var lexer = new SqlLexer(sql);
            var tokens = lexer.Tokenize();
            
            Assert.Contains(tokens, t => t.Type == TokenType.Select);
            Assert.Contains(tokens, t => t.Type == TokenType.Top);
            Assert.Contains(tokens, t => t.Type == TokenType.Identifier && t.Text.Equals("UPPER", StringComparison.OrdinalIgnoreCase));

            var parser = new TSqlParser(tokens);
            var stmt = parser.Parse();
            
            Assert.IsType<SelectStatement>(stmt);
            var select = (SelectStatement)stmt;
            Assert.Equal(5, select.Top);
            Assert.Equal(3, select.Projections.Count);
            Assert.Equal("users", ((SqlTableSource)select.From!).TableName);
            Assert.NotNull(select.Where);
            Assert.Single(select.OrderBy);
            Assert.True(select.OrderBy[0].Descending);
        }

        [Fact]
        public async Task TestDDLAndDML()
        {
            // 1. Create table
            string createSql = "CREATE TABLE employees (id INT, name VARCHAR(100), salary FLOAT, is_active BIT)";
            var createRes = await _engine.ExecuteAsync(createSql, _context);
            Assert.True(createRes.Success, createRes.Message);
            Assert.True(_catalog.TableExists("employees"));

            // Verify file initialized
            var meta = _catalog.GetTable("employees")!;
            Assert.True(File.Exists(meta.BackingFile));

            // 2. Insert rows
            string insertSql1 = "INSERT INTO employees (id, name, salary, is_active) VALUES (1, 'Alice', 75000.50, 1)";
            var insertRes1 = await _engine.ExecuteAsync(insertSql1, _context);
            Assert.True(insertRes1.Success, insertRes1.Message);
            Assert.Equal(1, insertRes1.AffectedRows);

            string insertSql2 = "INSERT INTO employees VALUES (2, 'Bob', 82000.00, 0)";
            var insertRes2 = await _engine.ExecuteAsync(insertSql2, _context);
            Assert.True(insertRes2.Success, insertRes2.Message);

            // 3. Query records
            string selectSql = "SELECT * FROM employees ORDER BY id ASC";
            var selectRes = await _engine.ExecuteAsync(selectSql, _context);
            Assert.True(selectRes.Success, selectRes.Message);
            Assert.NotNull(selectRes.DataFrame);
            
            var df = selectRes.DataFrame;
            Assert.Equal(2, df.RowCount);
            Assert.Equal(1, df.GetColumn("id").Get(0));
            Assert.Equal("Alice", df.GetColumn("name").Get(0));
            Assert.Equal(75000.50, df.GetColumn("salary").Get(0));
            Assert.Equal(true, df.GetColumn("is_active").Get(0));

            Assert.Equal(2, df.GetColumn("id").Get(1));
            Assert.Equal("Bob", df.GetColumn("name").Get(1));
            Assert.Equal(false, df.GetColumn("is_active").Get(1));
        }

        [Fact]
        public async Task TestAggregatesAndFunctions()
        {
            // Setup
            await _engine.ExecuteAsync("CREATE TABLE sales (id INT, qty INT, price FLOAT)", _context);
            await _engine.ExecuteAsync("INSERT INTO sales VALUES (1, 10, 5.0)", _context);
            await _engine.ExecuteAsync("INSERT INTO sales VALUES (2, 20, 10.0)", _context);
            await _engine.ExecuteAsync("INSERT INTO sales VALUES (3, 15, 10.0)", _context);

            // Query: Aggregations and math operators
            string query = "SELECT COUNT(*) AS total_sales, SUM(qty) AS total_qty, AVG(price) AS avg_price FROM sales";
            var res = await _engine.ExecuteAsync(query, _context);
            Assert.True(res.Success, res.Message);
            
            var df = res.DataFrame!;
            Assert.Equal(1, df.RowCount);
            Assert.Equal(3, df.GetColumn("total_sales").Get(0));
            Assert.Equal(45, df.GetColumn("total_qty").Get(0));
            Assert.Equal(8.333333333333334, df.GetColumn("avg_price").Get(0));
        }

        [Fact]
        public async Task TestJoinsAndFilters()
        {
            // Setup tables
            await _engine.ExecuteAsync("CREATE TABLE students (student_id INT, name VARCHAR)", _context);
            await _engine.ExecuteAsync("CREATE TABLE grades (student_id INT, score FLOAT)", _context);

            await _engine.ExecuteAsync("INSERT INTO students VALUES (1, 'John')", _context);
            await _engine.ExecuteAsync("INSERT INTO students VALUES (2, 'Jane')", _context);

            await _engine.ExecuteAsync("INSERT INTO grades VALUES (1, 95.5)", _context);
            await _engine.ExecuteAsync("INSERT INTO grades VALUES (2, 88.0)", _context);

            // Query with INNER JOIN
            string sql = "SELECT s.name, g.score FROM students AS s INNER JOIN grades AS g ON s.student_id = g.student_id WHERE g.score > 90.0";
            var res = await _engine.ExecuteAsync(sql, _context);
            Assert.True(res.Success, res.Message);

            var df = res.DataFrame!;
            Assert.Equal(1, df.RowCount);
            Assert.Equal("John", df.GetColumn("name").Get(0));
            Assert.Equal(95.5, df.GetColumn("score").Get(0));
        }

        [Fact]
        public async Task TestTriggersHook()
        {
            await _engine.ExecuteAsync("CREATE TABLE logger (log_message VARCHAR)", _context);
            await _engine.ExecuteAsync("CREATE TABLE data_table (id INT)", _context);

            // Add insert trigger
            _context.Triggers.Add(new SqlTrigger
            {
                Name = "LogInsertTrigger",
                TableName = "data_table",
                EventType = "INSERT",
                TriggerAction = (ctx, rows) =>
                {
                    int insertedId = (int)rows.GetColumn("id").Get(0)!;
                    string logSql = $"INSERT INTO logger VALUES ('Inserted ID: {insertedId}')";
                    var engine = new SqlEngine(ctx.Catalog);
                    engine.ExecuteAsync(logSql, ctx).GetAwaiter().GetResult();
                }
            });

            // Insert into data_table -> triggers trigger
            await _engine.ExecuteAsync("INSERT INTO data_table VALUES (42)", _context);

            // Check logger table
            var res = await _engine.ExecuteAsync("SELECT log_message FROM logger", _context);
            Assert.True(res.Success);
            Assert.Equal(1, res.DataFrame!.RowCount);
            Assert.Equal("Inserted ID: 42", res.DataFrame.GetColumn("log_message").Get(0));
        }

        [Fact]
        public async Task TestTransactionsHook()
        {
            await _engine.ExecuteAsync("CREATE TABLE bank (acc VARCHAR, balance INT)", _context);
            await _engine.ExecuteAsync("INSERT INTO bank VALUES ('A', 100)", _context);

            // Begin transaction
            var tx = new SqlTransaction();
            _context.ActiveTransaction = tx;

            // Perform modification
            await _engine.ExecuteAsync("INSERT INTO bank VALUES ('B', 200)", _context);

            // Verify both values present
            var resBefore = await _engine.ExecuteAsync("SELECT * FROM bank", _context);
            Assert.Equal(2, resBefore.DataFrame!.RowCount);

            // Rollback
            tx.Rollback();
            _context.ActiveTransaction = null;

            // Verify back to original
            var resAfter = await _engine.ExecuteAsync("SELECT * FROM bank", _context);
            Assert.Equal(1, resAfter.DataFrame!.RowCount);
            Assert.Equal("A", resAfter.DataFrame.GetColumn("acc").Get(0));
        }

        [Fact]
        public async Task TestPerformance_100kRows()
        {
            // 1. Create Table in catalog
            string tableName = "perf_table";
            _catalog.AddTable(tableName, new List<ColumnMetadata>
            {
                new ColumnMetadata { Name = "id", DataType = "INT" },
                new ColumnMetadata { Name = "value", DataType = "FLOAT" },
                new ColumnMetadata { Name = "category", DataType = "VARCHAR" }
            });
            var meta = _catalog.GetTable(tableName)!;

            // 2. Generate 100,000 rows in memory
            int numRows = 100000;
            var ids = new int[numRows];
            var values = new double[numRows];
            var categories = new string[numRows];
            var rand = new Random(42);
            string[] cats = { "CatA", "CatB", "CatC", "CatD" };

            for (int i = 0; i < numRows; i++)
            {
                ids[i] = i + 1;
                values[i] = rand.NextDouble();
                categories[i] = cats[rand.Next(cats.Length)];
            }

            var col1 = new Int32Series("id", numRows);
            ids.CopyTo(col1.Memory);
            
            var col2 = new Float64Series("value", numRows);
            values.CopyTo(col2.Memory);
            
            var col3 = new Utf8StringSeries("category", categories);

            var df = new DataFrame(new ISeries[] { col1, col2, col3 });

            // Measure Bulk Write Time
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TableStorage.WriteTable(df, meta.BackingFile);
            sw.Stop();
            long writeMs = sw.ElapsedMilliseconds;
            _output.WriteLine($"[Perf] Written {numRows} rows to Arrow IPC file in {writeMs}ms");

            // 3. Query 1: Filter & Projection
            sw.Restart();
            var q1 = await _engine.ExecuteAsync("SELECT id, value FROM perf_table WHERE value > 0.5", _context);
            sw.Stop();
            Assert.True(q1.Success);
            long q1Ms = sw.ElapsedMilliseconds;
            _output.WriteLine($"[Perf] Filter Query (value > 0.5) returned {q1.DataFrame!.RowCount} rows in {q1Ms}ms");

            // 4. Query 2: Group By & Aggregation
            sw.Restart();
            var q2 = await _engine.ExecuteAsync("SELECT category, COUNT(*) AS cnt, AVG(value) AS avg_val FROM perf_table GROUP BY category", _context);
            sw.Stop();
            Assert.True(q2.Success, q2.Message);
            long q2Ms = sw.ElapsedMilliseconds;
            _output.WriteLine($"[Perf] GroupBy/Agg Query returned {q2.DataFrame!.RowCount} rows in {q2Ms}ms");
            
            for (int r = 0; r < q2.DataFrame!.RowCount; r++)
            {
                _output.WriteLine($"   - {q2.DataFrame.GetColumn("category").Get(r)}: Count={q2.DataFrame.GetColumn("cnt").Get(r)}, Avg={q2.DataFrame.GetColumn("avg_val").Get(r)}");
            }

            // 5. Query 3: Sorting
            sw.Restart();
            var q3 = await _engine.ExecuteAsync("SELECT id, value FROM perf_table WHERE value < 0.01 ORDER BY value ASC", _context);
            sw.Stop();
            Assert.True(q3.Success);
            long q3Ms = sw.ElapsedMilliseconds;
            _output.WriteLine($"[Perf] Sort Query (value < 0.01 ORDER BY value ASC) returned {q3.DataFrame!.RowCount} rows in {q3Ms}ms");
        }

        [Fact]
        public async Task TestUpdateAndDelete()
        {
            // Setup
            await _engine.ExecuteAsync("CREATE TABLE test_mod (id INT, val INT, category VARCHAR)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_mod VALUES (1, 10, 'A')", _context);
            await _engine.ExecuteAsync("INSERT INTO test_mod VALUES (2, 20, 'B')", _context);
            await _engine.ExecuteAsync("INSERT INTO test_mod VALUES (3, 30, 'A')", _context);

            // 1. Test UPDATE
            var upRes = await _engine.ExecuteAsync("UPDATE test_mod SET val = val + 5 WHERE category = 'A'", _context);
            Assert.True(upRes.Success, upRes.Message);
            Assert.Equal(2, upRes.AffectedRows);

            // Verify update results
            var checkUp = await _engine.ExecuteAsync("SELECT * FROM test_mod ORDER BY id ASC", _context);
            var dfUp = checkUp.DataFrame!;
            Assert.Equal(15, dfUp.GetColumn("val").Get(0)); // 10 + 5
            Assert.Equal(20, dfUp.GetColumn("val").Get(1)); // Unchanged
            Assert.Equal(35, dfUp.GetColumn("val").Get(2)); // 30 + 5

            // 2. Test DELETE
            var delRes = await _engine.ExecuteAsync("DELETE FROM test_mod WHERE val > 25", _context);
            Assert.True(delRes.Success, delRes.Message);
            Assert.Equal(1, delRes.AffectedRows); // row 3 has val 35

            // Verify delete results
            var checkDel = await _engine.ExecuteAsync("SELECT * FROM test_mod ORDER BY id ASC", _context);
            var dfDel = checkDel.DataFrame!;
            Assert.Equal(2, dfDel.RowCount);
            Assert.Equal(1, dfDel.GetColumn("id").Get(0));
            Assert.Equal(2, dfDel.GetColumn("id").Get(1));
        }

        [Fact]
        public async Task TestUpdateAndDeleteTransactions()
        {
            await _engine.ExecuteAsync("CREATE TABLE tx_mod (id INT, status VARCHAR)", _context);
            await _engine.ExecuteAsync("INSERT INTO tx_mod VALUES (1, 'pending')", _context);
            await _engine.ExecuteAsync("INSERT INTO tx_mod VALUES (2, 'pending')", _context);

            // Begin Transaction
            var tx = new SqlTransaction();
            _context.ActiveTransaction = tx;

            // Run modifications
            await _engine.ExecuteAsync("UPDATE tx_mod SET status = 'completed' WHERE id = 1", _context);
            await _engine.ExecuteAsync("DELETE FROM tx_mod WHERE id = 2", _context);

            // Verify changes visible inside transaction
            var checkTx = await _engine.ExecuteAsync("SELECT status FROM tx_mod WHERE id = 1", _context);
            Assert.Equal("completed", checkTx.DataFrame!.GetColumn("status").Get(0));
            var countTx = await _engine.ExecuteAsync("SELECT * FROM tx_mod", _context);
            Assert.Equal(1, countTx.DataFrame!.RowCount);

            // Rollback
            tx.Rollback();
            _context.ActiveTransaction = null;

            // Verify changes rolled back
            var checkRollback = await _engine.ExecuteAsync("SELECT * FROM tx_mod ORDER BY id ASC", _context);
            var df = checkRollback.DataFrame!;
            Assert.Equal(2, df.RowCount);
            Assert.Equal("pending", df.GetColumn("status").Get(0));
            Assert.Equal("pending", df.GetColumn("status").Get(1));
        }

        [Fact]
        public async Task TestUpdateAndDeleteTriggers()
        {
            await _engine.ExecuteAsync("CREATE TABLE trig_test (id INT, val INT)", _context);
            await _engine.ExecuteAsync("CREATE TABLE trig_log (msg VARCHAR)", _context);

            await _engine.ExecuteAsync("INSERT INTO trig_test VALUES (1, 100)", _context);
            await _engine.ExecuteAsync("INSERT INTO trig_test VALUES (2, 200)", _context);

            // Trigger for UPDATE
            _context.Triggers.Add(new SqlTrigger
            {
                Name = "OnTrigUpdate",
                TableName = "trig_test",
                EventType = "UPDATE",
                TriggerAction = (ctx, rows) =>
                {
                    int updatedVal = (int)rows.GetColumn("val").Get(0)!;
                    string logSql = $"INSERT INTO trig_log VALUES ('Updated to: {updatedVal}')";
                    var engine = new SqlEngine(ctx.Catalog);
                    engine.ExecuteAsync(logSql, ctx).GetAwaiter().GetResult();
                }
            });

            // Trigger for DELETE
            _context.Triggers.Add(new SqlTrigger
            {
                Name = "OnTrigDelete",
                TableName = "trig_test",
                EventType = "DELETE",
                TriggerAction = (ctx, rows) =>
                {
                    int deletedId = (int)rows.GetColumn("id").Get(0)!;
                    string logSql = $"INSERT INTO trig_log VALUES ('Deleted ID: {deletedId}')";
                    var engine = new SqlEngine(ctx.Catalog);
                    engine.ExecuteAsync(logSql, ctx).GetAwaiter().GetResult();
                }
            });

            // Run UPDATE -> triggers OnTrigUpdate
            await _engine.ExecuteAsync("UPDATE trig_test SET val = 150 WHERE id = 1", _context);

            // Run DELETE -> triggers OnTrigDelete
            await _engine.ExecuteAsync("DELETE FROM trig_test WHERE id = 2", _context);

            // Verify log messages
            var checkLogs = await _engine.ExecuteAsync("SELECT msg FROM trig_log ORDER BY msg ASC", _context);
            var dfLogs = checkLogs.DataFrame!;
            Assert.Equal(2, dfLogs.RowCount);
            Assert.Equal("Deleted ID: 2", dfLogs.GetColumn("msg").Get(0));
            Assert.Equal("Updated to: 150", dfLogs.GetColumn("msg").Get(1));
        }

        [Fact]
        public async Task TestSqlNativeTransactions()
        {
            await _engine.ExecuteAsync("CREATE TABLE native_tx (id INT, name VARCHAR)", _context);
            await _engine.ExecuteAsync("INSERT INTO native_tx VALUES (1, 'Alice')", _context);

            // 1. Rollback test
            var resBegin1 = await _engine.ExecuteAsync("BEGIN TRANSACTION", _context);
            Assert.True(resBegin1.Success, resBegin1.Message);

            await _engine.ExecuteAsync("INSERT INTO native_tx VALUES (2, 'Bob')", _context);
            await _engine.ExecuteAsync("UPDATE native_tx SET name = 'Alice Updated' WHERE id = 1", _context);

            // Verify inside transaction changes are visible
            var checkInTx = await _engine.ExecuteAsync("SELECT * FROM native_tx ORDER BY id ASC", _context);
            Assert.Equal(2, checkInTx.DataFrame!.RowCount);
            Assert.Equal("Alice Updated", checkInTx.DataFrame.GetColumn("name").Get(0));

            var resRollback = await _engine.ExecuteAsync("ROLLBACK TRAN", _context);
            Assert.True(resRollback.Success, resRollback.Message);

            // Verify discarded outside
            var checkPostRollback = await _engine.ExecuteAsync("SELECT * FROM native_tx ORDER BY id ASC", _context);
            Assert.Equal(1, checkPostRollback.DataFrame!.RowCount);
            Assert.Equal("Alice", checkPostRollback.DataFrame.GetColumn("name").Get(0));

            // 2. Commit test
            var resBegin2 = await _engine.ExecuteAsync("BEGIN TRAN", _context);
            Assert.True(resBegin2.Success, resBegin2.Message);

            await _engine.ExecuteAsync("INSERT INTO native_tx VALUES (3, 'Charlie')", _context);
            
            var resCommit = await _engine.ExecuteAsync("COMMIT TRANSACTION", _context);
            Assert.True(resCommit.Success, resCommit.Message);

            // Verify committed changes
            var checkPostCommit = await _engine.ExecuteAsync("SELECT * FROM native_tx ORDER BY id ASC", _context);
            Assert.Equal(2, checkPostCommit.DataFrame!.RowCount);
            Assert.Equal(3, checkPostCommit.DataFrame.GetColumn("id").Get(1));

            // Verify backup files are deleted
            string backupFile = Path.Combine(_catalog.DataDirectory, "native_tx_backup.ipc");
            Assert.False(File.Exists(backupFile), "Backup file should be cleaned up on COMMIT.");
        }

        [Fact]
        public async Task TestCrashRecovery()
        {
            // Setup a table with some data
            string dataDir = Path.Combine(Path.GetTempPath(), "GlacierSqlCrashTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var catalog1 = new CatalogManager(dataDir);
                var engine1 = new SqlEngine(catalog1);
                var context1 = new Engine.ExecutionContext(catalog1);

                await engine1.ExecuteAsync("CREATE TABLE crash_table (id INT, val VARCHAR)", context1);
                await engine1.ExecuteAsync("INSERT INTO crash_table VALUES (1, 'Initial')", context1);

                var tableMeta = catalog1.GetTable("crash_table")!;
                string mainFile = tableMeta.BackingFile;
                string backupFile = Path.Combine(dataDir, "crash_table_backup.ipc");

                // 1. Manually simulate an uncommitted transaction state by creating a backup file 
                // and mutating the main file (as if the app crashed mid-modification).
                File.Copy(mainFile, backupFile, true);

                // Modify main file directly with mutated data
                var dfBackup = TableStorage.ReadTable(mainFile);
                var col1 = new Int32Series("id", 1);
                col1.Memory.Span[0] = 2;
                var col2 = new Utf8StringSeries("val", new[] { "CrashedState" });
                var mutatedDf = new DataFrame(new ISeries[] { col1, col2 });
                TableStorage.WriteTable(mutatedDf, mainFile);

                // 2. Load a NEW catalog manager pointing to the same folder.
                // This triggers the constructor -> Load() -> RecoverBackupFiles()!
                var catalog2 = new CatalogManager(dataDir);
                
                // 3. Verify that the table was automatically restored to the pre-crash state (id=1, val='Initial')
                var restoredDf = TableStorage.ReadTable(mainFile);
                Assert.Equal(1, restoredDf.RowCount);
                Assert.Equal(1, restoredDf.GetColumn("id").Get(0));
                Assert.Equal("Initial", restoredDf.GetColumn("val").Get(0));

                // Verify backup file was deleted after recovery
                Assert.False(File.Exists(backupFile), "Backup file should be deleted after successful recovery.");
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task TestInsertSelect()
        {
            await _engine.ExecuteAsync("CREATE TABLE source_tbl (id INT, val VARCHAR)", _context);
            await _engine.ExecuteAsync("CREATE TABLE target_tbl (id INT, val VARCHAR)", _context);

            await _engine.ExecuteAsync("INSERT INTO source_tbl VALUES (1, 'One')", _context);
            await _engine.ExecuteAsync("INSERT INTO source_tbl VALUES (2, 'Two')", _context);

            var res = await _engine.ExecuteAsync("INSERT INTO target_tbl SELECT id, val FROM source_tbl WHERE id = 2", _context);
            Assert.True(res.Success, res.Message);
            Assert.Equal(1, res.AffectedRows);

            var check = await _engine.ExecuteAsync("SELECT * FROM target_tbl", _context);
            Assert.Equal(1, check.DataFrame!.RowCount);
            Assert.Equal(2, check.DataFrame.GetColumn("id").Get(0));
            Assert.Equal("Two", check.DataFrame.GetColumn("val").Get(0));
        }

        [Fact]
        public async Task TestSqlNativeTriggers()
        {
            await _engine.ExecuteAsync("CREATE TABLE orders (id INT, qty INT)", _context);
            await _engine.ExecuteAsync("CREATE TABLE order_audit (id INT, qty INT, op VARCHAR)", _context);

            // 1. Create INSERT trigger
            var resTrig = await _engine.ExecuteAsync("CREATE TRIGGER audit_trig_ins ON orders AFTER INSERT AS INSERT INTO order_audit SELECT id, qty, 'INS' FROM inserted", _context);
            Assert.True(resTrig.Success, resTrig.Message);

            // 2. Create UPDATE trigger (using both inserted and deleted)
            var resTrigUp = await _engine.ExecuteAsync("CREATE TRIGGER audit_trig_up ON orders AFTER UPDATE AS INSERT INTO order_audit SELECT i.id, i.qty - d.qty, 'UP' FROM inserted i JOIN deleted d ON i.id = d.id", _context);
            Assert.True(resTrigUp.Success, resTrigUp.Message);

            // 3. Create DELETE trigger
            var resTrigDel = await _engine.ExecuteAsync("CREATE TRIGGER audit_trig_del ON orders AFTER DELETE AS INSERT INTO order_audit SELECT id, qty, 'DEL' FROM deleted", _context);
            Assert.True(resTrigDel.Success, resTrigDel.Message);

            // Test INSERT trigger
            await _engine.ExecuteAsync("INSERT INTO orders VALUES (1, 10)", _context);
            await _engine.ExecuteAsync("INSERT INTO orders VALUES (2, 20)", _context);

            var checkIns = await _engine.ExecuteAsync("SELECT * FROM order_audit ORDER BY id ASC", _context);
            var dfIns = checkIns.DataFrame!;
            Assert.Equal(2, dfIns.RowCount);
            Assert.Equal(1, dfIns.GetColumn("id").Get(0));
            Assert.Equal("INS", dfIns.GetColumn("op").Get(0));
            Assert.Equal(2, dfIns.GetColumn("id").Get(1));
            Assert.Equal("INS", dfIns.GetColumn("op").Get(1));

            // Test UPDATE trigger
            await _engine.ExecuteAsync("UPDATE orders SET qty = 25 WHERE id = 2", _context);

            var checkUp = await _engine.ExecuteAsync("SELECT * FROM order_audit WHERE op = 'UP'", _context);
            var dfUp = checkUp.DataFrame!;
            Assert.Equal(1, dfUp.RowCount);
            Assert.Equal(2, dfUp.GetColumn("id").Get(0));
            Assert.Equal(5, dfUp.GetColumn("qty").Get(0)); // 25 - 20 = 5

            // Test DELETE trigger
            await _engine.ExecuteAsync("DELETE FROM orders WHERE id = 1", _context);

            var checkDel = await _engine.ExecuteAsync("SELECT * FROM order_audit WHERE op = 'DEL'", _context);
            var dfDel = checkDel.DataFrame!;
            Assert.Equal(1, dfDel.RowCount);
            Assert.Equal(1, dfDel.GetColumn("id").Get(0));
            Assert.Equal(10, dfDel.GetColumn("qty").Get(0));
        }

        [Fact]
        public async Task TestTriggerPersistence()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "GlacierSqlTrigPersist_" + Guid.NewGuid().ToString("N"));
            try
            {
                var catalog1 = new CatalogManager(dataDir);
                var engine1 = new SqlEngine(catalog1);
                var context1 = new Engine.ExecutionContext(catalog1);

                await engine1.ExecuteAsync("CREATE TABLE orders (id INT, qty INT)", context1);
                await engine1.ExecuteAsync("CREATE TABLE audit (id INT, qty INT)", context1);

                // Create trigger
                await engine1.ExecuteAsync("CREATE TRIGGER persist_trig ON orders AFTER INSERT AS INSERT INTO audit SELECT id, qty FROM inserted", context1);

                // Reload catalog to test persistence
                var catalog2 = new CatalogManager(dataDir);
                var engine2 = new SqlEngine(catalog2);
                var context2 = new Engine.ExecutionContext(catalog2);

                // Verify trigger exists in catalog2
                Assert.True(catalog2.TriggerExists("persist_trig"));

                // Insert order and verify trigger fires using catalog2
                await engine2.ExecuteAsync("INSERT INTO orders VALUES (9, 99)", context2);

                var check = await engine2.ExecuteAsync("SELECT * FROM audit", context2);
                Assert.Equal(1, check.DataFrame!.RowCount);
                Assert.Equal(9, check.DataFrame.GetColumn("id").Get(0));
                Assert.Equal(99, check.DataFrame.GetColumn("qty").Get(0));
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task TestInsteadOfTrigger()
        {
            await _engine.ExecuteAsync("CREATE TABLE base_tbl (id INT, val VARCHAR)", _context);
            await _engine.ExecuteAsync("CREATE TABLE shadow_tbl (id INT, val VARCHAR)", _context);

            // Create INSTEAD OF INSERT trigger
            var resTrig = await _engine.ExecuteAsync("CREATE TRIGGER instead_trig ON base_tbl INSTEAD OF INSERT AS INSERT INTO shadow_tbl SELECT id, UPPER(val) FROM inserted", _context);
            Assert.True(resTrig.Success, resTrig.Message);

            // Insert into base_tbl
            var insertRes = await _engine.ExecuteAsync("INSERT INTO base_tbl VALUES (1, 'Alice')", _context);
            Assert.True(insertRes.Success, insertRes.Message);

            // Verify base_tbl is empty (since it was INSTEAD OF)
            var resBase = await _engine.ExecuteAsync("SELECT * FROM base_tbl", _context);
            Assert.Equal(0, resBase.DataFrame!.RowCount);

            // Verify shadow_tbl has the modified row
            var resShadow = await _engine.ExecuteAsync("SELECT * FROM shadow_tbl", _context);
            Assert.Equal(1, resShadow.DataFrame!.RowCount);
            Assert.Equal(1, resShadow.DataFrame.GetColumn("id").Get(0));
            Assert.Equal("ALICE", resShadow.DataFrame.GetColumn("val").Get(0));
        }

        [Fact]
        public async Task TestTriggerRollbackInTransaction()
        {
            await _engine.ExecuteAsync("CREATE TABLE orders (id INT, qty INT)", _context);
            await _engine.ExecuteAsync("CREATE TABLE order_audit (id INT, qty INT)", _context);

            await _engine.ExecuteAsync("CREATE TRIGGER tr ON orders AFTER INSERT AS INSERT INTO order_audit SELECT id, qty FROM inserted", _context);

            // Begin
            await _engine.ExecuteAsync("BEGIN TRANSACTION", _context);

            await _engine.ExecuteAsync("INSERT INTO orders VALUES (1, 10)", _context);

            // Verify both updated inside transaction
            var checkOrd = await _engine.ExecuteAsync("SELECT * FROM orders", _context);
            var checkAud = await _engine.ExecuteAsync("SELECT * FROM order_audit", _context);
            Assert.Equal(1, checkOrd.DataFrame!.RowCount);
            Assert.Equal(1, checkAud.DataFrame!.RowCount);

            // Rollback
            await _engine.ExecuteAsync("ROLLBACK TRAN", _context);

            // Verify both empty
            var checkOrdPost = await _engine.ExecuteAsync("SELECT * FROM orders", _context);
            var checkAudPost = await _engine.ExecuteAsync("SELECT * FROM order_audit", _context);
            Assert.Equal(0, checkOrdPost.DataFrame!.RowCount);
            Assert.Equal(0, checkAudPost.DataFrame!.RowCount);
        }

        [Fact]
        public async Task TestCascadingTriggers()
        {
            await _engine.ExecuteAsync("CREATE TABLE t1 (id INT)", _context);
            await _engine.ExecuteAsync("CREATE TABLE t2 (id INT)", _context);
            await _engine.ExecuteAsync("CREATE TABLE t3 (id INT)", _context);

            await _engine.ExecuteAsync("CREATE TRIGGER tr1 ON t1 AFTER INSERT AS INSERT INTO t2 SELECT id FROM inserted", _context);
            await _engine.ExecuteAsync("CREATE TRIGGER tr2 ON t2 AFTER INSERT AS INSERT INTO t3 SELECT id FROM inserted", _context);

            var res = await _engine.ExecuteAsync("INSERT INTO t1 VALUES (100)", _context);
            Assert.True(res.Success, res.Message);

            var check1 = await _engine.ExecuteAsync("SELECT * FROM t1", _context);
            var check2 = await _engine.ExecuteAsync("SELECT * FROM t2", _context);
            var check3 = await _engine.ExecuteAsync("SELECT * FROM t3", _context);

            Assert.Equal(1, check1.DataFrame!.RowCount);
            Assert.Equal(1, check2.DataFrame!.RowCount);
            Assert.Equal(1, check3.DataFrame!.RowCount);

            Assert.Equal(100, check3.DataFrame.GetColumn("id").Get(0));
        }

        [Fact]
        public async Task TestMultiColumnUpdate()
        {
            await _engine.ExecuteAsync("CREATE TABLE test_multi (id INT, price FLOAT, qty INT)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_multi VALUES (1, 10.0, 5)", _context);

            var res = await _engine.ExecuteAsync("UPDATE test_multi SET price = price * 2, qty = qty + 10 WHERE id = 1", _context);
            Assert.True(res.Success, res.Message);
            Assert.Equal(1, res.AffectedRows);

            var check = await _engine.ExecuteAsync("SELECT * FROM test_multi", _context);
            var df = check.DataFrame!;
            Assert.Equal(20.0, df.GetColumn("price").Get(0));
            Assert.Equal(15, df.GetColumn("qty").Get(0));
        }

        [Fact]
        public async Task TestNullAggregation()
        {
            await _engine.ExecuteAsync("CREATE TABLE test_nulls (id INT, qty INT)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_nulls VALUES (1, 10)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_nulls VALUES (2, NULL)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_nulls VALUES (3, 20)", _context);

            var res = await _engine.ExecuteAsync("SELECT COUNT(*) AS c_all, COUNT(qty) AS c_qty, SUM(qty) AS s_qty, AVG(qty) AS a_qty FROM test_nulls", _context);
            Assert.True(res.Success, res.Message);

            var df = res.DataFrame!;
            Assert.Equal(3, df.GetColumn("c_all").Get(0));
            Assert.Equal(2, df.GetColumn("c_qty").Get(0));
            Assert.Equal(30, df.GetColumn("s_qty").Get(0));
            Assert.Equal(15.0, df.GetColumn("a_qty").Get(0));
        }
        [Fact]
        public async Task TestConstraintsEnforcement()
        {
            // NOT NULL and PRIMARY KEY
            await _engine.ExecuteAsync("CREATE TABLE test_const (id INT PRIMARY KEY, name VARCHAR NOT NULL, age INT CHECK (age >= 18), email VARCHAR UNIQUE)", _context);

            // Happy path
            var res1 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (1, 'Alice', 20, 'alice@test.com')", _context);
            Assert.True(res1.Success, res1.Message);

            // Test PK duplicate error
            var res2 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (1, 'Bob', 25, 'bob@test.com')", _context);
            Assert.False(res2.Success);
            Assert.Contains("Violation of UNIQUE/PRIMARY KEY constraint", res2.Message);

            // Test NOT NULL constraint error
            var res3 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (2, NULL, 25, 'bob@test.com')", _context);
            Assert.False(res3.Success);
            Assert.Contains("Cannot insert or update NULL into NOT NULL column", res3.Message);

            // Test CHECK constraint error
            var res4 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (2, 'Bob', 15, 'bob@test.com')", _context);
            Assert.False(res4.Success);
            Assert.Contains("Violation of CHECK constraint", res4.Message);

            // Test UNIQUE constraint error
            var res5 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (3, 'Bob', 25, 'alice@test.com')", _context);
            Assert.False(res5.Success);
            Assert.Contains("Violation of UNIQUE/PRIMARY KEY constraint", res5.Message);

            // Test happy path with nulls for check and unique
            var res6 = await _engine.ExecuteAsync("INSERT INTO test_const VALUES (2, 'Bob', NULL, NULL)", _context);
            Assert.True(res6.Success);
        }

        [Fact]
        public async Task TestAlterTable()
        {
            await _engine.ExecuteAsync("CREATE TABLE test_alter (id INT PRIMARY KEY, name VARCHAR)", _context);
            await _engine.ExecuteAsync("INSERT INTO test_alter VALUES (1, 'Alice')", _context);

            // 1. ADD COLUMN
            var alterAdd = await _engine.ExecuteAsync("ALTER TABLE test_alter ADD age INT CHECK (age >= 18)", _context);
            Assert.True(alterAdd.Success, alterAdd.Message);

            // Verify metadata
            var meta = _catalog.GetTable("test_alter")!;
            Assert.Contains(meta.Columns, c => c.Name.Equals("age", StringComparison.OrdinalIgnoreCase));

            // Select and verify it is NULL
            var sel1 = await _engine.ExecuteAsync("SELECT age FROM test_alter WHERE id = 1", _context);
            Assert.True(sel1.Success);
            Assert.Null(sel1.DataFrame!.GetColumn("age").Get(0));

            // Update new column and verify
            await _engine.ExecuteAsync("UPDATE test_alter SET age = 20 WHERE id = 1", _context);
            var sel2 = await _engine.ExecuteAsync("SELECT age FROM test_alter WHERE id = 1", _context);
            Assert.Equal(20, sel2.DataFrame!.GetColumn("age").Get(0));

            // Try violating CHECK constraint on new column
            var badUp = await _engine.ExecuteAsync("UPDATE test_alter SET age = 15 WHERE id = 1", _context);
            Assert.False(badUp.Success);

            // 2. DROP COLUMN
            var alterDrop = await _engine.ExecuteAsync("ALTER TABLE test_alter DROP COLUMN age", _context);
            Assert.True(alterDrop.Success, alterDrop.Message);

            var metaPost = _catalog.GetTable("test_alter")!;
            Assert.DoesNotContain(metaPost.Columns, c => c.Name.Equals("age", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task TestSqlViews()
        {
            await _engine.ExecuteAsync("CREATE TABLE orders_v (order_id INT, customer_id INT, amount FLOAT)", _context);
            await _engine.ExecuteAsync("INSERT INTO orders_v VALUES (101, 1, 150.0)", _context);
            await _engine.ExecuteAsync("INSERT INTO orders_v VALUES (102, 2, 80.0)", _context);
            await _engine.ExecuteAsync("INSERT INTO orders_v VALUES (103, 1, 200.0)", _context);

            // CREATE VIEW
            var cvRes = await _engine.ExecuteAsync("CREATE VIEW v_customer_summary AS SELECT customer_id, COUNT(*) AS count_orders, SUM(amount) AS total_amount FROM orders_v GROUP BY customer_id", _context);
            Assert.True(cvRes.Success, cvRes.Message);
            Assert.True(_catalog.ViewExists("v_customer_summary"));

            // SELECT FROM VIEW
            var selRes = await _engine.ExecuteAsync("SELECT * FROM v_customer_summary WHERE customer_id = 1", _context);
            Assert.True(selRes.Success, selRes.Message);
            
            var df = selRes.DataFrame!;
            Assert.Equal(1, df.RowCount);
            Assert.Equal(2, df.GetColumn("count_orders").Get(0));
            Assert.Equal(350.0, df.GetColumn("total_amount").Get(0));

            // DROP VIEW
            var dvRes = await _engine.ExecuteAsync("DROP VIEW v_customer_summary", _context);
            Assert.True(dvRes.Success, dvRes.Message);
            Assert.False(_catalog.ViewExists("v_customer_summary"));
        }

        [Fact]
        public async Task TestSubqueries()
        {
            await _engine.ExecuteAsync("CREATE TABLE t_a (id INT, val VARCHAR)", _context);
            await _engine.ExecuteAsync("CREATE TABLE t_b (id INT, score INT)", _context);

            await _engine.ExecuteAsync("INSERT INTO t_a VALUES (1, 'Apple')", _context);
            await _engine.ExecuteAsync("INSERT INTO t_a VALUES (2, 'Banana')", _context);
            await _engine.ExecuteAsync("INSERT INTO t_a VALUES (3, 'Cherry')", _context);

            await _engine.ExecuteAsync("INSERT INTO t_b VALUES (1, 90)", _context);
            await _engine.ExecuteAsync("INSERT INTO t_b VALUES (2, 80)", _context);

            // 1. Scalar subquery in select projection and filter
            var resScalar = await _engine.ExecuteAsync("SELECT val, (SELECT MAX(score) FROM t_b) AS max_s FROM t_a WHERE id = (SELECT MIN(id) FROM t_b)", _context);
            Assert.True(resScalar.Success, resScalar.Message);
            var dfScalar = resScalar.DataFrame!;
            Assert.Equal(1, dfScalar.RowCount);
            Assert.Equal("Apple", dfScalar.GetColumn("val").Get(0));
            Assert.Equal(90, dfScalar.GetColumn("max_s").Get(0));

            // 2. IN subquery
            var resIn = await _engine.ExecuteAsync("SELECT val FROM t_a WHERE id IN (SELECT id FROM t_b WHERE score > 85)", _context);
            Assert.True(resIn.Success, resIn.Message);
            var dfIn = resIn.DataFrame!;
            Assert.Equal(1, dfIn.RowCount);
            Assert.Equal("Apple", dfIn.GetColumn("val").Get(0));

            // 3. EXISTS subquery
            var resExists = await _engine.ExecuteAsync("SELECT val FROM t_a WHERE EXISTS (SELECT * FROM t_b WHERE t_b.id = t_a.id AND score >= 90)", _context);
            Assert.True(resExists.Success, resExists.Message);
            var dfExists = resExists.DataFrame!;
            Assert.Equal(1, dfExists.RowCount);
            Assert.Equal("Apple", dfExists.GetColumn("val").Get(0));

            // 4. NOT EXISTS subquery
            var resNotExists = await _engine.ExecuteAsync("SELECT val FROM t_a WHERE NOT EXISTS (SELECT * FROM t_b WHERE t_b.id = t_a.id)", _context);
            Assert.True(resNotExists.Success, resNotExists.Message);
            var dfNotExists = resNotExists.DataFrame!;
            Assert.Equal(1, dfNotExists.RowCount);
            Assert.Equal("Cherry", dfNotExists.GetColumn("val").Get(0));
        }

        [Fact]
        public async Task TestInformationSchema()
        {
            await _engine.ExecuteAsync("CREATE TABLE t_info (id INT PRIMARY KEY, name VARCHAR NOT NULL)", _context);

            // Query TABLES
            var resTables = await _engine.ExecuteAsync("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't_info'", _context);
            Assert.True(resTables.Success, resTables.Message);
            Assert.Equal(1, resTables.DataFrame!.RowCount);
            Assert.Equal("t_info", resTables.DataFrame.GetColumn("TABLE_NAME").Get(0));

            // Query COLUMNS
            var resCols = await _engine.ExecuteAsync("SELECT COLUMN_NAME, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't_info' ORDER BY COLUMN_NAME ASC", _context);
            Assert.True(resCols.Success, resCols.Message);
            var df = resCols.DataFrame!;
            Assert.Equal(2, df.RowCount);
            Assert.Equal("id", df.GetColumn("COLUMN_NAME").Get(0));
            Assert.Equal("NO", df.GetColumn("IS_NULLABLE").Get(0)); // PK is not nullable
            Assert.Equal("name", df.GetColumn("COLUMN_NAME").Get(1));
            Assert.Equal("NO", df.GetColumn("IS_NULLABLE").Get(1));
        }

        [Fact]
        public async Task TestConcurrencyLocking()
        {
            await _engine.ExecuteAsync("CREATE TABLE t_lock (id INT, val VARCHAR)", _context);
            await _engine.ExecuteAsync("INSERT INTO t_lock VALUES (1, 'initial')", _context);

            // Start a transaction in context 1 (acquiring write lock on t_lock)
            var tx = new SqlTransaction();
            _context.ActiveTransaction = tx;
            await _engine.ExecuteAsync("UPDATE t_lock SET val = 'tx_value' WHERE id = 1", _context);

            // Run a parallel task trying to read t_lock
            var readContext = new Engine.ExecutionContext(_catalog);
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var readTask = Task.Run(async () =>
            {
                var engine2 = new SqlEngine(_catalog);
                return await engine2.ExecuteAsync("SELECT val FROM t_lock WHERE id = 1", readContext);
            });

            // Wait 200ms to verify it is blocked
            await Task.Delay(200);
            Assert.False(readTask.IsCompleted);

            // Now commit transaction
            tx.Commit();
            _context.ActiveTransaction = null;

            // Now wait for the read task to finish
            var readRes = await readTask;
            sw.Stop();

            Assert.True(readRes.Success, readRes.Message);
            Assert.Equal("tx_value", readRes.DataFrame!.GetColumn("val").Get(0));
            Assert.True(sw.ElapsedMilliseconds >= 200);
        }
    }
}
