using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Host.Mcp;
using Xunit;

namespace Glacier.Sql.Tests
{
    public class SqlHostTests : IDisposable
    {
        private readonly string _tempDir;

        public SqlHostTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"glacier_sql_host_tests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        [Fact]
        public void SqlMcpServer_Constructor_RejectsNullArguments()
        {
            using var catalog = new CatalogManager(_tempDir);
            var engine = new SqlEngine(catalog);

            Assert.Throws<ArgumentNullException>(() => new SqlMcpServer(null!, engine, new MemoryStream(), new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => new SqlMcpServer(catalog, null!, new MemoryStream(), new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => new SqlMcpServer(catalog, engine, null!, new MemoryStream()));
            Assert.Throws<ArgumentNullException>(() => new SqlMcpServer(catalog, engine, new MemoryStream(), null!));
        }

        [Fact]
        public async Task HostRunAsync_SupportsInitializeToolsListAndSqlWorkflow()
        {
            string requests = string.Join("\n", new[]
            {
                """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","method":"notifications/cancelled"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"CREATE TABLE users (id INT, name VARCHAR)"}}}""",
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"INSERT INTO users VALUES (1, 'Alice')"}}}""",
                """{"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"INSERT INTO users VALUES (2, 'Bob')"}}}""",
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"SELECT id, name FROM users ORDER BY id"}}}""",
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"SELECT id, name FROM users WHERE id = 999"}}}""",
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"list_tables","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_table_schema","arguments":{"tableName":"users"}}}""",
                """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_table_schema","arguments":{"tableName":"missing_table"}}}"""
            }) + "\n";

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
            using var output = new MemoryStream();

            await Glacier.Sql.Host.Program.RunAsync(Array.Empty<string>(), _tempDir, input, output);

            var responses = ParseLines(Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal(10, responses.Count);

            // 1: initialize (reports assembly version 1.1.1, not hardcoded 1.1.0)
            Assert.Equal("2024-11-05", responses[0].RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.Equal("Glacier.Sql", responses[0].RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            Assert.NotEqual("1.1.0", responses[0].RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString());

            // 2: tools/list (includes maxResults and offset pagination parameters on execute_sql)
            var toolsArray = responses[1].RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
            var toolNames = toolsArray
                .Select(t => t.GetProperty("name").GetString() ?? string.Empty)
                .ToHashSet();
            Assert.Equal(new[] { "execute_sql", "list_tables", "get_table_schema" }.ToHashSet(), toolNames);
            var execProps = toolsArray.First(t => t.GetProperty("name").GetString() == "execute_sql")
                .GetProperty("inputSchema").GetProperty("properties");
            Assert.True(execProps.TryGetProperty("maxResults", out _));
            Assert.True(execProps.TryGetProperty("offset", out _));

            // 3..6: CREATE, INSERT x2, SELECT
            Assert.DoesNotContain("ERROR:", GetToolText(responses[2]));
            Assert.DoesNotContain("ERROR:", GetToolText(responses[3]));
            Assert.DoesNotContain("ERROR:", GetToolText(responses[4]));
            string selectMarkdown = GetToolText(responses[5]);
            Assert.Contains("| id | name |", selectMarkdown);
            Assert.Contains("Alice", selectMarkdown);
            Assert.Contains("Bob", selectMarkdown);

            // 7: Empty set
            Assert.Equal("Empty Set (0 rows)\n\nColumns: id, name", GetToolText(responses[6]));

            // 8: list_tables
            string tablesList = GetToolText(responses[7]);
            Assert.Contains("### Registered Tables", tablesList);
            Assert.Contains("users", tablesList);

            // 9: get_table_schema (found)
            string schema = GetToolText(responses[8]);
            Assert.Contains("### Table: users", schema);
            Assert.Contains("| id |", schema);
            Assert.Contains("| name |", schema);

            // 10: get_table_schema (not found) sets isError=true
            Assert.Contains("ERROR: Table 'missing_table' not found", GetToolText(responses[9]));
            Assert.True(responses[9].RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        [Fact]
        public async Task Host_Pagination_IsError_DataDirAndLogGating_WorkCleanly()
        {
            string? prevDataDirEnv = Environment.GetEnvironmentVariable("GLACIER_SQL_DATA_DIR");
            string? prevLogEnv = Environment.GetEnvironmentVariable("GLACIER_SQL_MCP_LOG");
            try
            {
                Environment.SetEnvironmentVariable("GLACIER_SQL_DATA_DIR", _tempDir);
                Environment.SetEnvironmentVariable("GLACIER_SQL_MCP_LOG", null);

                // Verify ResolveDataDirectory precedence
                Assert.Equal("explicit", Glacier.Sql.Host.Program.ResolveDataDirectory(new[] { "--data-dir", "cli" }, "explicit"));
                Assert.Equal("cli", Glacier.Sql.Host.Program.ResolveDataDirectory(new[] { "--data-dir", "cli" }));
                Assert.Equal("cli_eq", Glacier.Sql.Host.Program.ResolveDataDirectory(new[] { "--data-dir=cli_eq" }));
                Assert.Equal(_tempDir, Glacier.Sql.Host.Program.ResolveDataDirectory(Array.Empty<string>()));

                Environment.SetEnvironmentVariable("GLACIER_SQL_DATA_DIR", null);
                string fallback = Glacier.Sql.Host.Program.ResolveDataDirectory(Array.Empty<string>());
                Assert.False(fallback.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase));

                // Verify mcp_sql_log.txt is disabled when GLACIER_SQL_MCP_LOG is unset
                Assert.Null(SqlMcpServer.ResolveLogPath(_tempDir));

                // Verify pagination (maxResults + offset) and isError=true on invalid SQL
                var reqs = new List<string>
                {
                    """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"CREATE TABLE paged (id INT)"}}}""",
                    """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"INSERT INTO paged VALUES (1),(2),(3),(4),(5)"}}}""",
                    """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"SELECT id FROM paged ORDER BY id ASC","maxResults":2,"offset":1}}}""",
                    """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"SELECT * FROM missing_tbl_err"}}}"""
                };
                string payload = string.Join("\n", reqs) + "\n";

                using var input = new MemoryStream(Encoding.UTF8.GetBytes(payload));
                using var output = new MemoryStream();
                await Glacier.Sql.Host.Program.RunAsync(Array.Empty<string>(), _tempDir, input, output);

                var responses = ParseLines(Encoding.UTF8.GetString(output.ToArray()));
                Assert.Equal(4, responses.Count);

                string pagedMd = GetToolText(responses[2]);
                Assert.DoesNotContain("| 1 |", pagedMd);
                Assert.Contains("| 2 |", pagedMd);
                Assert.Contains("| 3 |", pagedMd);
                Assert.DoesNotContain("| 4 |", pagedMd);
                Assert.Contains("Showing rows 2-3 of 5", pagedMd);

                Assert.True(responses[3].RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
                Assert.False(File.Exists(Path.Combine(_tempDir, "mcp_sql_log.txt")));
                Assert.False(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_sql_log.txt")));

                // When GLACIER_SQL_MCP_LOG=1, log is written inside _tempDir (not BaseDirectory)
                Environment.SetEnvironmentVariable("GLACIER_SQL_MCP_LOG", "1");
                string? enabledPath = SqlMcpServer.ResolveLogPath(_tempDir);
                Assert.Equal(Path.Combine(_tempDir, "mcp_sql_log.txt"), enabledPath);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GLACIER_SQL_DATA_DIR", prevDataDirEnv);
                Environment.SetEnvironmentVariable("GLACIER_SQL_MCP_LOG", prevLogEnv);
            }
        }

        [Fact]
        public async Task HostRunAsync_HandlesSqlErrorsAndInvalidJsonRpcRequests()
        {
            string requests = string.Join("\n", new[]
            {
                "",
                "{invalid-json",
                """{"jsonrpc":"2.0","id":1}""",
                """{"jsonrpc":"2.0","id":2,"method":"unknown/method"}""",
                """{"jsonrpc":"2.0","method":"unknown/notification"}""",
                """{"jsonrpc":"2.0","id":3,"method":"tools/call"}""",
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"unsupported_tool","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"execute_sql","arguments":{}}}""",
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"execute_sql","arguments":{"sql":"SELECT * FROM nonexistent_tbl"}}}"""
            }) + "\n";

            using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
            using var output = new MemoryStream();

            await Glacier.Sql.Host.Program.RunAsync(Array.Empty<string>(), _tempDir, input, output);

            var responses = ParseLines(Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal(5, responses.Count);

            Assert.Equal(-32601, GetErrorCode(responses[0])); // unknown/method
            Assert.Equal(-32602, GetErrorCode(responses[1])); // missing params
            Assert.Equal(-32000, GetErrorCode(responses[2])); // unsupported tool
            Assert.Equal(-32000, GetErrorCode(responses[3])); // missing sql property
            Assert.Contains("ERROR:", GetToolText(responses[4])); // SQL execution error

            // Malformed JSON without id produces no response; verify id-bearing malformed JSON returns -32603
            string malformedWithId = """{"jsonrpc":"2.0","id":99,"method":"tools/call","params":bad}""" + "\n";
            using var input2 = new MemoryStream(Encoding.UTF8.GetBytes(malformedWithId));
            using var output2 = new MemoryStream();
            await Glacier.Sql.Host.Program.RunAsync(Array.Empty<string>(), _tempDir, input2, output2);
            Assert.Empty(ParseLines(Encoding.UTF8.GetString(output2.ToArray())));
        }

        private static List<JsonDocument> ParseLines(string output)
        {
            return output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line))
                .ToList();
        }

        private static string GetToolText(JsonDocument doc)
        {
            return doc.RootElement
                .GetProperty("result")
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;
        }

        private static int GetErrorCode(JsonDocument doc)
        {
            return doc.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetInt32();
        }
    }
}
