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

        internal static string ResolveDataDirectory(string[]? args, string? explicitDataDir = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitDataDir))
            {
                return explicitDataDir;
            }

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg.Equals("--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        if (!string.IsNullOrWhiteSpace(args[i + 1]))
                        {
                            return args[i + 1];
                        }
                    }
                    else if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = arg.Substring("--data-dir=".Length);
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return val;
                        }
                    }
                    else if (!arg.StartsWith("-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(arg))
                    {
                        return arg;
                    }
                }
            }

            string? envDir = Environment.GetEnvironmentVariable("GLACIER_SQL_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                return envDir;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "Glacier", "Sql", "Data");
            }

            return Path.Combine(Path.GetTempPath(), "Glacier", "Sql", "Data");
        }

        internal static async Task RunAsync(string[] args, string? dataDir = null, Stream? inputStream = null, Stream? outputStream = null)
        {
            string resolvedDataDir = ResolveDataDirectory(args, dataDir);
            try
            {
                using var catalog = new CatalogManager(resolvedDataDir);
                var engine = new SqlEngine(catalog);
                using var server = inputStream != null && outputStream != null
                    ? new SqlMcpServer(catalog, engine, inputStream, outputStream)
                    : new SqlMcpServer(catalog, engine);

                // Run the stdio JSON-RPC loop
                await server.RunAsync();
                catalog.Checkpoint();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTimeOffset.UtcNow:O}] CRITICAL CRASH: {ex.Message}\n{ex.StackTrace}");

                string? logSetting = Environment.GetEnvironmentVariable("GLACIER_SQL_MCP_LOG");
                if (!string.IsNullOrWhiteSpace(logSetting) &&
                    !logSetting.Equals("0", StringComparison.OrdinalIgnoreCase) &&
                    !logSetting.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string? logDir = (logSetting.Equals("1", StringComparison.OrdinalIgnoreCase) || logSetting.Equals("true", StringComparison.OrdinalIgnoreCase))
                            ? resolvedDataDir
                            : Path.GetDirectoryName(logSetting);
                        if (!string.IsNullOrEmpty(logDir))
                        {
                            Directory.CreateDirectory(logDir);
                            string crashPath = Path.Combine(logDir, "critical_error.txt");
                            File.WriteAllText(crashPath, $"[{DateTimeOffset.UtcNow:O}] CRITICAL CRASH: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    catch
                    {
                        // Best-effort crash logging when explicitly enabled
                    }
                }

                if (inputStream != null || outputStream != null)
                {
                    throw;
                }
            }
        }
    }
}
