using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Glacier.Polaris;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.BufferPool;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Catalog
{
    public class TableMetadata
    {
        public string TableName { get; set; } = "";
        public List<ColumnMetadata> Columns { get; set; } = new();
        public string BackingFile { get; set; } = "";
    }

    public class ColumnMetadata
    {
        public string Name { get; set; } = "";
        public string DataType { get; set; } = ""; // "INT", "FLOAT", "VARCHAR", "BIT", "DATETIME"
        public bool IsNullable { get; set; } = true;
        public bool IsPrimaryKey { get; set; } = false;
        public bool IsUnique { get; set; } = false;
        public string? CheckExpression { get; set; } = null;
    }

    public class TriggerMetadata
    {
        public string TriggerName { get; set; } = "";
        public string TableName { get; set; } = "";
        public string EventType { get; set; } = ""; // "INSERT", "UPDATE", "DELETE"
        public string Timing { get; set; } = ""; // "AFTER" or "INSTEAD OF"
        public string ActionSql { get; set; } = "";
    }

    public class ViewMetadata
    {
        public string ViewName { get; set; } = "";
        public string DefinitionSql { get; set; } = "";
    }

    public class CatalogData
    {
        public List<TableMetadata> Tables { get; set; } = new();
        public List<TriggerMetadata> Triggers { get; set; } = new();
        public List<ViewMetadata> Views { get; set; } = new();
    }

    public class CatalogManager : IDisposable
    {
        private readonly string _catalogPath;
        private readonly string _dataDir;
        private readonly Dictionary<string, TableMetadata> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TriggerMetadata> _triggers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ViewMetadata> _views = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ThreadIndependentReaderWriterLock> _tableLocks = new(StringComparer.OrdinalIgnoreCase);
        private WalWriter? _walWriter;

        private readonly bool _ownsBufferPool;

        public string DataDirectory => _dataDir;
        public TableBufferPool BufferPool { get; }
        public WalWriter WalWriter => _walWriter ??= new WalWriter(Path.Combine(_dataDir, "glacier.wal"));

        public CatalogManager(string? baseDir = null, TableBufferPool? bufferPool = null)
        {
            // By default, place the database data under the workspace/project 'Data' folder
            baseDir ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _dataDir = baseDir;
            _catalogPath = Path.Combine(_dataDir, "catalog.json");
            _ownsBufferPool = bufferPool == null;
            BufferPool = bufferPool ?? new TableBufferPool();

            Load();
        }

        public void Load()
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                if (File.Exists(_catalogPath))
                {
                    string json = File.ReadAllText(_catalogPath).Trim();
                    List<TableMetadata>? metadataList = null;
                    List<TriggerMetadata>? triggerList = null;
                    CatalogData? catalogData = null;

                    if (json.StartsWith("["))
                    {
                        // Legacy format: raw TableMetadata list
                        metadataList = JsonSerializer.Deserialize<List<TableMetadata>>(json);
                    }
                    else
                    {
                        // New CatalogData format
                        catalogData = JsonSerializer.Deserialize<CatalogData>(json);
                        if (catalogData != null)
                        {
                            metadataList = catalogData.Tables;
                            triggerList = catalogData.Triggers;
                        }
                    }

                    _tables.Clear();
                    if (metadataList != null)
                    {
                        foreach (var meta in metadataList)
                        {
                            _tables[meta.TableName] = meta;
                        }
                    }

                    _triggers.Clear();
                    if (triggerList != null)
                    {
                        foreach (var trig in triggerList)
                        {
                            _triggers[trig.TriggerName] = trig;
                        }
                    }

                    _views.Clear();
                    if (catalogData != null && catalogData.Views != null)
                    {
                        foreach (var view in catalogData.Views)
                        {
                            _views[view.ViewName] = view;
                        }
                    }
                }

                // Recover any orphaned transaction backups from a previous crash
                RecoverBackupFiles();

                // Recover committed mutations from Write-Ahead Log
                RecoverFromWal();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading catalog: {ex.Message}");
            }
        }

        private void RecoverBackupFiles()
        {
            try
            {
                if (!Directory.Exists(_dataDir)) return;

                var backupFiles = Directory.GetFiles(_dataDir, "*_backup.ipc");
                foreach (var backupPath in backupFiles)
                {
                    string fileName = Path.GetFileName(backupPath);
                    int backupIndex = fileName.LastIndexOf("_backup.ipc", StringComparison.OrdinalIgnoreCase);
                    if (backupIndex <= 0) continue;

                    string tableName = fileName.Substring(0, backupIndex);
                    var meta = GetTable(tableName);
                    if (meta != null)
                    {
                        try
                        {
                            File.Copy(backupPath, meta.BackingFile, true);
                            File.Delete(backupPath);
                            BufferPool.Invalidate(tableName);
                            TableStorage.BufferPool.Invalidate(tableName);
                            Console.WriteLine($"[Recovery] Restored table '{tableName}' from transaction backup due to previous crash.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Recovery] Failed to restore '{tableName}': {ex.Message}");
                        }
                    }
                    else
                    {
                        // Clean up backup if table no longer exists in catalog
                        try { File.Delete(backupPath); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error recovering transaction backups: {ex.Message}");
            }
        }

        private void RecoverFromWal()
        {
            try
            {
                string walPath = Path.Combine(_dataDir, "glacier.wal");
                if (!File.Exists(walPath)) return;

                var records = WalReader.Read(walPath, out ulong checkpointLsn);
                if (records.Count == 0) return;

                var uncheckpointed = records.Where(r => r.Lsn > checkpointLsn).ToList();
                if (uncheckpointed.Count == 0) return;

                var committedTx = new HashSet<Guid>();
                foreach (var r in uncheckpointed)
                {
                    if (r.Type == WalRecordType.TxCommit)
                    {
                        committedTx.Add(r.TxId);
                    }
                }

                int replayedCount = 0;
                foreach (var r in uncheckpointed)
                {
                    // Skip uncommitted multi-statement transactions
                    if (r.TxId != Guid.Empty && !committedTx.Contains(r.TxId))
                    {
                        continue;
                    }

                    if (r.Type == WalRecordType.RowInsert)
                    {
                        var meta = GetTable(r.TableName);
                        if (meta != null)
                        {
                            var entry = BufferPool.GetOrLoad(meta);
                            var insertedDf = TableStorage.DeserializeDataFrame(r.Payload);
                            using (entry.AcquireWriteLock())
                            {
                                entry.CurrentSnapshot = DataFrame.Concat(new[] { entry.CurrentSnapshot, insertedDf });
                                entry.IsDirty = true;
                                entry.LastAppliedLsn = r.Lsn;
                            }
                            replayedCount++;
                        }
                    }
                    else if (r.Type == WalRecordType.RowDelete)
                    {
                        var meta = GetTable(r.TableName);
                        if (meta != null)
                        {
                            var entry = BufferPool.GetOrLoad(meta);
                            string deleteSql = Encoding.UTF8.GetString(r.Payload);
                            using (entry.AcquireWriteLock())
                            {
                                if (string.IsNullOrWhiteSpace(deleteSql))
                                {
                                    entry.CurrentSnapshot = TableStorage.CreateEmptyDataFrame(meta.Columns);
                                }
                                else
                                {
                                    var lexer = new SqlLexer(deleteSql);
                                    var tokens = lexer.Tokenize();
                                    var parser = new TSqlParser(tokens);
                                    var stmt = parser.Parse();
                                    if (stmt is DeleteStatement del)
                                    {
                                        if (del.Where == null)
                                        {
                                            entry.CurrentSnapshot = TableStorage.CreateEmptyDataFrame(meta.Columns);
                                        }
                                        else
                                        {
                                            var planner = new QueryPlanner(this);
                                            var conditionExpr = planner.CompileExpression(del.Where, del.TableName);
                                            var keepExpr = conditionExpr.IsNull() | (conditionExpr == Expr.Lit(false));
                                            entry.CurrentSnapshot = entry.CurrentSnapshot.Lazy().Filter(keepExpr).Collect().GetAwaiter().GetResult();
                                        }
                                    }
                                }
                                entry.IsDirty = true;
                                entry.LastAppliedLsn = r.Lsn;
                            }
                            replayedCount++;
                        }
                    }
                    else if (r.Type == WalRecordType.RowUpdate)
                    {
                        var meta = GetTable(r.TableName);
                        if (meta != null)
                        {
                            var entry = BufferPool.GetOrLoad(meta);
                            string updateSql = Encoding.UTF8.GetString(r.Payload);
                            using (entry.AcquireWriteLock())
                            {
                                var lexer = new SqlLexer(updateSql);
                                var tokens = lexer.Tokenize();
                                var parser = new TSqlParser(tokens);
                                var stmt = parser.Parse();
                                if (stmt is UpdateStatement update)
                                {
                                    var planner = new QueryPlanner(this);
                                    var conditionExpr = update.Where != null 
                                        ? planner.CompileExpression(update.Where, update.TableName)
                                        : Expr.Lit(true);

                                    var updateMap = update.Assignments.ToDictionary(a => a.ColumnName, a => a.Expression, StringComparer.OrdinalIgnoreCase);
                                    var projections = new List<Expr>();
                                    foreach (var col in meta.Columns)
                                    {
                                        if (updateMap.TryGetValue(col.Name, out var assignExpr))
                                        {
                                            var valExpr = planner.CompileExpression(assignExpr, update.TableName);
                                            var condProj = Expr.When(conditionExpr).Then(valExpr).Otherwise(Expr.Col(col.Name)).Alias(col.Name);
                                            projections.Add(condProj);
                                        }
                                        else
                                        {
                                            projections.Add(Expr.Col(col.Name).Alias(col.Name));
                                        }
                                    }
                                    entry.CurrentSnapshot = entry.CurrentSnapshot.Lazy().Select(projections.ToArray()).Collect().GetAwaiter().GetResult();
                                }
                                entry.IsDirty = true;
                                entry.LastAppliedLsn = r.Lsn;
                            }
                            replayedCount++;
                        }
                    }
                }

                if (replayedCount > 0)
                {
                    BufferPool.CheckpointAll(WalWriter);
                    Console.WriteLine($"[WAL Recovery] Successfully replayed {replayedCount} committed mutation records up to LSN {checkpointLsn}.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WAL Recovery] Error during recovery: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                var catalogData = new CatalogData
                {
                    Tables = new List<TableMetadata>(_tables.Values),
                    Triggers = new List<TriggerMetadata>(_triggers.Values),
                    Views = new List<ViewMetadata>(_views.Values)
                };
                string json = JsonSerializer.Serialize(catalogData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_catalogPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving catalog: {ex.Message}");
            }
        }

        public bool TableExists(string tableName)
        {
            return _tables.ContainsKey(tableName);
        }

        public TableMetadata? GetTable(string tableName)
        {
            return _tables.TryGetValue(tableName, out var meta) ? meta : null;
        }

        public IEnumerable<TableMetadata> ListTables()
        {
            return _tables.Values;
        }

        public void AddTable(string tableName, List<ColumnMetadata> columns)
        {
            var meta = new TableMetadata
            {
                TableName = tableName,
                Columns = columns,
                BackingFile = Path.Combine(_dataDir, $"{tableName}.ipc")
            };
            _tables[tableName] = meta;
            Save();
            TableStorage.InitializeTable(meta.BackingFile, columns);
            BufferPool.GetOrLoad(meta);
        }

        public void RemoveTable(string tableName)
        {
            if (_tables.TryGetValue(tableName, out var meta))
            {
                _tables.Remove(tableName);
                BufferPool.Invalidate(tableName);

                // Clean up any triggers associated with this table
                var triggersToRemove = new List<string>();
                foreach (var trig in _triggers.Values)
                {
                    if (trig.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        triggersToRemove.Add(trig.TriggerName);
                    }
                }
                foreach (var name in triggersToRemove)
                {
                    _triggers.Remove(name);
                }

                Save();

                // Delete backing file if it exists
                if (File.Exists(meta.BackingFile))
                {
                    try
                    {
                        File.Delete(meta.BackingFile);
                    }
                    catch { /* Ignore delete errors */ }
                }
            }
        }

        // Trigger Operations
        public void AddTrigger(TriggerMetadata trig)
        {
            _triggers[trig.TriggerName] = trig;
            Save();
        }

        public void RemoveTrigger(string triggerName)
        {
            if (_triggers.Remove(triggerName))
            {
                Save();
            }
        }

        public List<TriggerMetadata> GetTriggersForTable(string tableName, string eventType)
        {
            var result = new List<TriggerMetadata>();
            foreach (var trig in _triggers.Values)
            {
                if (trig.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                    trig.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(trig);
                }
            }
            return result;
        }

        public bool TriggerExists(string triggerName)
        {
            return _triggers.ContainsKey(triggerName);
        }

        // View Operations
        public void AddView(string viewName, string definitionSql)
        {
            var meta = new ViewMetadata
            {
                ViewName = viewName,
                DefinitionSql = definitionSql
            };
            _views[viewName] = meta;
            Save();
        }

        public void RemoveView(string viewName)
        {
            if (_views.Remove(viewName))
            {
                Save();
            }
        }

        public ViewMetadata? GetView(string viewName)
        {
            return _views.TryGetValue(viewName, out var meta) ? meta : null;
        }

        public bool ViewExists(string viewName)
        {
            return _views.ContainsKey(viewName);
        }

        public IEnumerable<ViewMetadata> ListViews()
        {
            return _views.Values;
        }

        // Lock Operations
        public ThreadIndependentReaderWriterLock GetTableLock(string tableName)
        {
            lock (_tableLocks)
            {
                if (!_tableLocks.TryGetValue(tableName, out var lk))
                {
                    lk = new ThreadIndependentReaderWriterLock();
                    _tableLocks[tableName] = lk;
                }
                return lk;
            }
        }

        public void Checkpoint()
        {
            BufferPool.CheckpointAll(WalWriter);
        }

        public void Dispose()
        {
            _walWriter?.Dispose();
            if (_ownsBufferPool)
            {
                BufferPool.Dispose();
            }
        }
    }
}
