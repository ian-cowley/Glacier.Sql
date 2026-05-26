using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;

namespace Glacier.Sql.Host.Mcp
{
    public class SqlMcpServer
    {
        private readonly CatalogManager _catalog;
        private readonly SqlEngine _engine;
        private readonly Engine.ExecutionContext _context;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _logPath;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:T}] {message}\n");
            }
            catch { /* Ignore logging errors */ }
        }

        public SqlMcpServer(CatalogManager catalog, SqlEngine engine)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _context = new Engine.ExecutionContext(_catalog);
            _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp_sql_log.txt");

            Log("SQL MCP Server instance created.");

            // Setup standard IO for MCP communication
            Stream openStandardInput = Console.OpenStandardInput();
            _reader = new StreamReader(openStandardInput);

            Stream openStandardOutput = Console.OpenStandardOutput();
            _writer = new StreamWriter(openStandardOutput, new UTF8Encoding(false)) { AutoFlush = true };
            _writer.NewLine = "\n";
            
            Log("IO setup complete.");
        }

        public async Task RunAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonElement requestId = default;
                try
                {
                    Log($"Received: {line}");
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("method", out JsonElement methodElem))
                    {
                        Log("Message missing 'method' property.");
                        continue;
                    }

                    string method = methodElem.GetString() ?? "";
                    root.TryGetProperty("id", out requestId);

                    Log($"Method: {method}");

                    switch (method)
                    {
                        case "initialize":
                            await SendResponseAsync(requestId, HandleInitialize());
                            break;

                        case "notifications/initialized":
                            Log("Initialized notification received.");
                            break;

                        case "notifications/cancelled":
                            Log("Client cancelled a request.");
                            break;

                        case "tools/list":
                            await SendResponseAsync(requestId, HandleToolsList());
                            break;

                        case "tools/call":
                            try
                            {
                                if (root.TryGetProperty("params", out JsonElement parameters))
                                {
                                    var result = await HandleToolCallAsync(parameters);
                                    await SendResponseAsync(requestId, result);
                                }
                                else
                                {
                                    await SendErrorAsync(requestId, -32602, "Missing parameters for tools/call");
                                }
                            }
                            catch (Exception toolEx)
                            {
                                Log($"Tool execution error: {toolEx.Message}");
                                await SendErrorAsync(requestId, -32000, toolEx.Message);
                            }
                            break;

                        default:
                            Log($"Method '{method}' not handled.");
                            if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                            {
                                await SendErrorAsync(requestId, -32601, $"Method '{method}' not found.");
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
                    if (requestId.ValueKind != JsonValueKind.Undefined && requestId.ValueKind != JsonValueKind.Null)
                    {
                        try { await SendErrorAsync(requestId, -32603, "Internal server error."); } catch { }
                    }
                }
            }
        }

        private object HandleInitialize()
        {
            return new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "Glacier.Sql", version = "1.0.0" }
            };
        }

        private object HandleToolsList()
        {
            return new
            {
                tools = new[]
                {
                    new Dictionary<string, object>
                    {
                        { "name", "execute_sql" },
                        { "description", "Executes a T-SQL query (e.g. SELECT, INSERT, CREATE TABLE, DROP TABLE) against the Glacier database and returns tabular results formatted as a Markdown table." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "sql", new Dictionary<string, object> { { "type", "string" }, { "description", "The SQL text to execute." } } }
                                    }
                                },
                                { "required", new[] { "sql" } }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "list_tables" },
                        { "description", "Lists all schemas and table storage statistics inside the database." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>() }
                            }
                        }
                    },
                    new Dictionary<string, object>
                    {
                        { "name", "get_table_schema" },
                        { "description", "Retrieves metadata and columns definition details for a specific table." },
                        { "inputSchema", new Dictionary<string, object>
                            {
                                { "type", "object" },
                                { "properties", new Dictionary<string, object>
                                    {
                                        { "tableName", new Dictionary<string, object> { { "type", "string" }, { "description", "The name of the table to inspect." } } }
                                    }
                                },
                                { "required", new[] { "tableName" } }
                            }
                        }
                    }
                }
            };
        }

        private async Task<object> HandleToolCallAsync(JsonElement parameters)
        {
            string name = parameters.GetProperty("name").GetString() ?? "";
            JsonElement args = parameters.GetProperty("arguments");

            if (name == "execute_sql")
            {
                string sql = args.GetProperty("sql").GetString()!;
                var res = await _engine.ExecuteAsync(sql, _context);

                if (!res.Success)
                {
                    return CreateToolResponse($"ERROR: {res.Message}");
                }

                if (res.DataFrame != null)
                {
                    string md = FormatDataFrameAsMarkdown(res.DataFrame);
                    return CreateToolResponse(md);
                }

                return CreateToolResponse(res.Message);
            }
            else if (name == "list_tables")
            {
                var tables = _catalog.ListTables();
                var sb = new StringBuilder();
                sb.AppendLine("### Registered Tables");
                sb.AppendLine("| Table Name | Columns | Backing File | Rows |");
                sb.AppendLine("| --- | --- | --- | --- |");

                foreach (var t in tables)
                {
                    int rowCount = 0;
                    try
                    {
                        var df = TableStorage.ReadTable(t.BackingFile);
                        rowCount = df.RowCount;
                    }
                    catch { }

                    string colsStr = string.Join(", ", t.Columns.Select(c => $"{c.Name} ({c.DataType})"));
                    sb.AppendLine($"| {t.TableName} | {colsStr} | {Path.GetFileName(t.BackingFile)} | {rowCount} |");
                }

                return CreateToolResponse(sb.ToString());
            }
            else if (name == "get_table_schema")
            {
                string tableName = args.GetProperty("tableName").GetString()!;
                var meta = _catalog.GetTable(tableName);
                if (meta == null)
                {
                    return CreateToolResponse($"ERROR: Table '{tableName}' not found in catalog.");
                }

                var sb = new StringBuilder();
                sb.AppendLine($"### Table: {meta.TableName}");
                sb.AppendLine("| Column Name | Data Type |");
                sb.AppendLine("| --- | --- |");
                foreach (var c in meta.Columns)
                {
                    sb.AppendLine($"| {c.Name} | {c.DataType} |");
                }

                return CreateToolResponse(sb.ToString());
            }

            throw new Exception($"Tool '{name}' is not supported.");
        }

        private static object CreateToolResponse(string text)
        {
            return new
            {
                content = new[]
                {
                    new { type = "text", text = text }
                }
            };
        }

        private string FormatDataFrameAsMarkdown(DataFrame df)
        {
            if (df.Columns.Count == 0) return "No columns returned.";
            if (df.RowCount == 0)
            {
                return $"Empty Set (0 rows)\n\nColumns: {string.Join(", ", df.Columns.Select(c => c.Name))}";
            }

            var sb = new StringBuilder();
            
            // Header
            sb.Append("| ");
            foreach (var col in df.Columns)
            {
                sb.Append(col.Name).Append(" | ");
            }
            sb.AppendLine();

            // Divider
            sb.Append("| ");
            foreach (var col in df.Columns)
            {
                sb.Append("--- | ");
            }
            sb.AppendLine();

            // Rows
            for (int i = 0; i < df.RowCount; i++)
            {
                sb.Append("| ");
                foreach (var col in df.Columns)
                {
                    object? val = col.Get(i);
                    string cellStr;
                    if (val == null)
                    {
                        cellStr = "NULL";
                    }
                    else if (col is TimeSeries && val is long ms)
                    {
                        // Format Epoch milliseconds to DateTime string
                        var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).DateTime;
                        cellStr = dt.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                    else
                    {
                        cellStr = val.ToString()!;
                    }

                    cellStr = cellStr.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
                    sb.Append(cellStr).Append(" | ");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private async Task SendResponseAsync(JsonElement id, object result)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["result"] = result
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendErrorAsync(JsonElement id, int code, string message)
        {
            var response = new Dictionary<string, object>
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new { code, message }
            };

            if (id.ValueKind != JsonValueKind.Undefined)
            {
                response["id"] = id;
            }

            string json = JsonSerializer.Serialize(response, JsonOpts);
            Log($"Sending error: {json}");
            await _writer.WriteLineAsync(json);
        }
    }
}
