using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Host.Mcp;

namespace Glacier.Sql.Host
{
    class Program
    {
        static async Task Main(string[] args)
        {
            await RunAsync(args);
        }

        internal static async Task RunAsync(string[] args, string? dataDir = null, Stream? inputStream = null, Stream? outputStream = null)
        {
            _ = args;
            try
            {
                // Place the SQLite-like file catalog and binary Arrow IPC tables under the current run directory's 'Data' subfolder
                string resolvedDataDir = dataDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

                using var catalog = new CatalogManager(resolvedDataDir);
                var engine = new SqlEngine(catalog);
                var server = inputStream != null && outputStream != null
                    ? new SqlMcpServer(catalog, engine, inputStream, outputStream)
                    : new SqlMcpServer(catalog, engine);

                // Run the stdio JSON-RPC loop
                await server.RunAsync();
                catalog.Checkpoint();
            }
            catch (Exception ex)
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "critical_error.txt");
                File.WriteAllText(logPath, $"[{DateTime.Now}] CRITICAL CRASH: {ex.Message}\n{ex.StackTrace}");
                if (inputStream != null || outputStream != null)
                {
                    throw;
                }
            }
        }
    }
}
