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
            try
            {
                // Place the SQLite-like file catalog and binary Arrow IPC tables under the current run directory's 'Data' subfolder
                string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                
                var catalog = new CatalogManager(dataDir);
                var engine = new SqlEngine(catalog);
                var server = new SqlMcpServer(catalog, engine);

                // Run the stdio JSON-RPC loop
                await server.RunAsync();
            }
            catch (Exception ex)
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "critical_error.txt");
                File.WriteAllText(logPath, $"[{DateTime.Now}] CRITICAL CRASH: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
