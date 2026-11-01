using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;

namespace Glacier.Sql.Host.Mcp
{
    internal sealed class McpRpcResponse<TResult>
    {
        public string Jsonrpc { get; set; } = "2.0";
        public JsonElement? Id { get; set; }
        public TResult Result { get; set; } = default!;
    }

    internal sealed class McpErrorResponse
    {
        public string Jsonrpc { get; set; } = "2.0";
        public JsonElement? Id { get; set; }
        public McpErrorDetail Error { get; set; } = new();
    }

    internal sealed class McpErrorDetail
    {
        public int Code { get; set; }
        public string Message { get; set; } = "";
    }

    internal sealed class McpInitializeResult
    {
        public string ProtocolVersion { get; set; } = "2024-11-05";
        public McpCapabilities Capabilities { get; set; } = new();
        public McpServerInfo ServerInfo { get; set; } = new();
    }

    internal sealed class McpCapabilities
    {
        public McpToolsCapability Tools { get; set; } = new();
    }

    internal sealed class McpToolsCapability
    {
        public bool ListChanged { get; set; }
    }

    internal sealed class McpServerInfo
    {
        public string Name { get; set; } = "Glacier.Sql";
        public string Version { get; set; } = "1.1.0";
    }

    internal sealed class McpToolsListResult
    {
        public McpToolDefinition[] Tools { get; set; } = Array.Empty<McpToolDefinition>();
    }

    internal sealed class McpToolDefinition
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public McpInputSchema InputSchema { get; set; } = new();
    }

    internal sealed class McpInputSchema
    {
        public string Type { get; set; } = "object";
        public Dictionary<string, McpPropertySchema> Properties { get; set; } = new();
        public string[]? Required { get; set; }
    }

    internal sealed class McpPropertySchema
    {
        public string Type { get; set; } = "string";
        public string Description { get; set; } = "";
    }

    internal sealed class McpToolCallResult
    {
        public McpContentItem[] Content { get; set; } = Array.Empty<McpContentItem>();
    }

    internal sealed class McpContentItem
    {
        public string Type { get; set; } = "text";
        public string Text { get; set; } = "";
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(McpRpcResponse<McpInitializeResult>))]
    [JsonSerializable(typeof(McpRpcResponse<McpToolsListResult>))]
    [JsonSerializable(typeof(McpRpcResponse<McpToolCallResult>))]
    [JsonSerializable(typeof(McpErrorResponse))]
    internal partial class SqlMcpJsonContext : JsonSerializerContext
    {
    }

    public class SqlMcpServer
    {
        private readonly CatalogManager _catalog;
        private readonly SqlEngine _engine;
        private readonly Engine.ExecutionContext _context;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly string _logPath;

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
                            await SendResponseAsync(
                                requestId,
                                HandleInitialize(),
                                SqlMcpJsonContext.Default.McpRpcResponseMcpInitializeResult);
                            break;

                        case "notifications/initialized":
                            Log("Initialized notification received.");
                            break;

                        case "notifications/cancelled":
                            Log("Client cancelled a request.");
                            break;

                        case "tools/list":
                            await SendResponseAsync(
                                requestId,
                                HandleToolsList(),
                                SqlMcpJsonContext.Default.McpRpcResponseMcpToolsListResult);
                            break;

                        case "tools/call":
                            try
                            {
                                if (root.TryGetProperty("params", out JsonElement parameters))
                                {
                                    var result = await HandleToolCallAsync(parameters);
                                    await SendResponseAsync(
                                        requestId,
                                        result,
                                        SqlMcpJsonContext.Default.McpRpcResponseMcpToolCallResult);
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

        private static McpInitializeResult HandleInitialize()
        {
            return new McpInitializeResult
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new McpCapabilities
                {
                    Tools = new McpToolsCapability { ListChanged = false }
                },
                ServerInfo = new McpServerInfo
                {
                    Name = "Glacier.Sql",
                    Version = "1.1.0"
                }
            };
        }

        private static McpToolsListResult HandleToolsList()
        {
            return new McpToolsListResult
            {
                Tools = new[]
                {
                    new McpToolDefinition
                    {
                        Name = "execute_sql",
                        Description = "Executes a T-SQL query (e.g. SELECT, INSERT, CREATE TABLE, DROP TABLE) against the Glacier database and returns tabular results formatted as a Markdown table.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["sql"] = new McpPropertySchema
                                {
                                    Type = "string",
                                    Description = "The SQL text to execute."
                                }
                            },
                            Required = new[] { "sql" }
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "list_tables",
                        Description = "Lists all schemas and table storage statistics inside the database.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>()
                        }
                    },
                    new McpToolDefinition
                    {
                        Name = "get_table_schema",
                        Description = "Retrieves metadata and columns definition details for a specific table.",
                        InputSchema = new McpInputSchema
                        {
                            Type = "object",
                            Properties = new Dictionary<string, McpPropertySchema>
                            {
                                ["tableName"] = new McpPropertySchema
                                {
                                    Type = "string",
                                    Description = "The name of the table to inspect."
                                }
                            },
                            Required = new[] { "tableName" }
                        }
                    }
                }
            };
        }

        private async Task<McpToolCallResult> HandleToolCallAsync(JsonElement parameters)
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
                        var entry = _catalog.BufferPool.GetOrLoad(t);
                        using (entry.AcquireReadLock())
                        {
                            rowCount = entry.CurrentSnapshot.RowCount;
                        }
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

        private static McpToolCallResult CreateToolResponse(string text)
        {
            return new McpToolCallResult
            {
                Content = new[]
                {
                    new McpContentItem { Type = "text", Text = text }
                }
            };
        }

        private static string FormatDataFrameAsMarkdown(DataFrame df)
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
            foreach (var _ in df.Columns)
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

        private async Task SendResponseAsync<TResult>(
            JsonElement id,
            TResult result,
            JsonTypeInfo<McpRpcResponse<TResult>> typeInfo)
        {
            var response = new McpRpcResponse<TResult>
            {
                Jsonrpc = "2.0",
                Id = id.ValueKind != JsonValueKind.Undefined ? id.Clone() : null,
                Result = result
            };

            string json = JsonSerializer.Serialize(response, typeInfo);
            Log($"Sending response: {json}");
            await _writer.WriteLineAsync(json);
        }

        private async Task SendErrorAsync(JsonElement id, int code, string message)
        {
            var response = new McpErrorResponse
            {
                Jsonrpc = "2.0",
                Id = id.ValueKind != JsonValueKind.Undefined ? id.Clone() : null,
                Error = new McpErrorDetail { Code = code, Message = message }
            };

            string json = JsonSerializer.Serialize(response, SqlMcpJsonContext.Default.McpErrorResponse);
            Log($"Sending error: {json}");
            await _writer.WriteLineAsync(json);
        }
    }
}
