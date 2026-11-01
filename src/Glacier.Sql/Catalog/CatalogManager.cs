using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Glacier.Polaris;
using Glacier.Sql.Diagnostics;
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
        private readonly object _catalogLock = new();
        private readonly object _walInitLock = new();
        private readonly Dictionary<string, TableMetadata> _tables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TriggerMetadata> _triggers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ViewMetadata> _views = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AsyncReaderWriterLock> _tableLocks = new(StringComparer.OrdinalIgnoreCase);
        private WalWriter? _walWriter;

        private readonly bool _ownsBufferPool;

        public string DataDirectory => _dataDir;
        public TableBufferPool BufferPool { get; }

        public WalWriter WalWriter
        {
            get
            {
                if (_walWriter != null) return _walWriter;
                lock (_walInitLock)
                {
                    return _walWriter ??= new WalWriter(Path.Combine(_dataDir, "glacier.wal"));
                }
            }
        }

        public CatalogManager(string? baseDir = null, TableBufferPool? bufferPool = null)
        {
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

                    if (json.StartsWith('['))
                    {
                        metadataList = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.ListTableMetadata);
                    }
                    else
                    {
                        catalogData = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogData);
                        if (catalogData != null)
                        {
                            metadataList = catalogData.Tables;
                            triggerList = catalogData.Triggers;
                        }
                    }

                    lock (_catalogLock)
                    {
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
                        if (catalogData?.Views != null)
                        {
                            foreach (var view in catalogData.Views)
                            {
                                _views[view.ViewName] = view;
                            }
                        }
                    }
                }

                // Recover committed mutations from Write-Ahead Log
                RecoverFromWal();
            }
            catch (Exception ex)
            {
                GlacierSqlDiagnostics.LogError($"Error loading catalog: {ex.Message}", ex);
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

                var committedTx = new HashSet<Guid>();
                var flushedTableLsns = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

                foreach (var r in records)
                {
                    if (r.Type == WalRecordType.TxCommit && r.TxId != Guid.Empty)
                    {
                        committedTx.Add(r.TxId);
                    }
                    else if (r.Type == WalRecordType.CheckpointEnd && !string.IsNullOrEmpty(r.TableName))
                    {
                        ulong tableLsn = r.Payload.Length >= 8
                            ? BitConverter.ToUInt64(r.Payload.AsSpan(0, 8))
                            : r.Lsn;

                        if (!flushedTableLsns.TryGetValue(r.TableName, out ulong prev) || tableLsn > prev)
                        {
                            flushedTableLsns[r.TableName] = tableLsn;
                        }
                    }
                }

                var uncheckpointed = records.Where(r => r.Lsn > checkpointLsn).ToList();
                if (uncheckpointed.Count == 0) return;

                int replayedCount = 0;
                foreach (var r in uncheckpointed)
                {
                    if (r.TxId != Guid.Empty && !committedTx.Contains(r.TxId))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(r.TableName) &&
                        flushedTableLsns.TryGetValue(r.TableName, out ulong tableFlushedLsn) &&
                        r.Lsn <= tableFlushedLsn)
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
                            if (insertedDf.Columns.Count > 0)
                            {
                                using (entry.AcquireWriteLock())
                                {
                                    var merged = DataFrame.Concat(new[] { entry.CurrentSnapshot, insertedDf });
                                    entry.ApplyMutation(merged, r.Lsn, Guid.Empty);
                                }
                                replayedCount++;
                            }
                        }
                    }
                    else if (r.Type == WalRecordType.RowUpdate || r.Type == WalRecordType.RowDelete)
                    {
                        var meta = GetTable(r.TableName);
                        if (meta != null)
                        {
                            var entry = BufferPool.GetOrLoad(meta);
                            var snapshotDf = TableStorage.DeserializeDataFrame(r.Payload);
                            if (snapshotDf.Columns.Count == 0)
                            {
                                snapshotDf = TableStorage.CreateEmptyDataFrame(meta.Columns);
                            }

                            using (entry.AcquireWriteLock())
                            {
                                entry.ApplyMutation(snapshotDf, r.Lsn, Guid.Empty);
                            }
                            replayedCount++;
                        }
                    }
                }

                if (replayedCount > 0)
                {
                    BufferPool.CheckpointAll(WalWriter);
                    GlacierSqlDiagnostics.LogInformation($"[WAL Recovery] Successfully replayed {replayedCount} committed mutation records (previous checkpoint LSN {checkpointLsn}).");
                }
            }
            catch (Exception ex)
            {
                GlacierSqlDiagnostics.LogError($"[WAL Recovery] Error during recovery: {ex.Message}", ex);
            }
        }

        public void Save()
        {
            string? tmpPath = null;
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                CatalogData catalogData;
                lock (_catalogLock)
                {
                    catalogData = new CatalogData
                    {
                        Tables = _tables.Values.Select(CloneTableMetadata).ToList(),
                        Triggers = new List<TriggerMetadata>(_triggers.Values),
                        Views = new List<ViewMetadata>(_views.Values)
                    };
                }

                string json = JsonSerializer.Serialize(catalogData, CatalogJsonContext.Default.CatalogData);
                tmpPath = _catalogPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _catalogPath, overwrite: true);
            }
            catch (Exception ex)
            {
                GlacierSqlDiagnostics.LogError($"Error saving catalog: {ex.Message}", ex);
            }
            finally
            {
                if (tmpPath != null && File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static TableMetadata CloneTableMetadata(TableMetadata src)
        {
            return new TableMetadata
            {
                TableName = src.TableName,
                BackingFile = src.BackingFile,
                Columns = new List<ColumnMetadata>(src.Columns)
            };
        }

        public bool TableExists(string tableName)
        {
            lock (_catalogLock)
            {
                return _tables.ContainsKey(tableName);
            }
        }

        public TableMetadata? GetTable(string tableName)
        {
            lock (_catalogLock)
            {
                return _tables.TryGetValue(tableName, out var meta) ? meta : null;
            }
        }

        public IEnumerable<TableMetadata> ListTables()
        {
            lock (_catalogLock)
            {
                return _tables.Values.ToList();
            }
        }

        public void AddTable(string tableName, List<ColumnMetadata> columns)
        {
            var meta = new TableMetadata
            {
                TableName = tableName,
                Columns = columns,
                BackingFile = Path.Combine(_dataDir, $"{tableName}.ipc")
            };

            TableStorage.InitializeTable(meta.BackingFile, columns);

            lock (_catalogLock)
            {
                _tables[tableName] = meta;
            }

            BufferPool.Invalidate(tableName);
            Save();
        }

        public void RemoveTable(string tableName)
        {
            TableMetadata? meta;
            lock (_catalogLock)
            {
                if (!_tables.TryGetValue(tableName, out meta))
                {
                    return;
                }

                _tables.Remove(tableName);

                var triggersToRemove = _triggers.Values
                    .Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.TriggerName)
                    .ToList();

                foreach (var name in triggersToRemove)
                {
                    _triggers.Remove(name);
                }
            }

            BufferPool.Invalidate(tableName);
            Save();

            if (File.Exists(meta.BackingFile))
            {
                try
                {
                    File.Delete(meta.BackingFile);
                }
                catch { /* Ignore delete errors */ }
            }
        }

        // Trigger Operations
        public void AddTrigger(TriggerMetadata trig)
        {
            lock (_catalogLock)
            {
                _triggers[trig.TriggerName] = trig;
            }
            Save();
        }

        public void RemoveTrigger(string triggerName)
        {
            bool removed;
            lock (_catalogLock)
            {
                removed = _triggers.Remove(triggerName);
            }
            if (removed)
            {
                Save();
            }
        }

        public List<TriggerMetadata> GetTriggersForTable(string tableName, string eventType)
        {
            lock (_catalogLock)
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
        }

        public bool TriggerExists(string triggerName)
        {
            lock (_catalogLock)
            {
                return _triggers.ContainsKey(triggerName);
            }
        }

        // View Operations
        public void AddView(string viewName, string definitionSql)
        {
            var meta = new ViewMetadata
            {
                ViewName = viewName,
                DefinitionSql = definitionSql
            };
            lock (_catalogLock)
            {
                _views[viewName] = meta;
            }
            Save();
        }

        public void RemoveView(string viewName)
        {
            bool removed;
            lock (_catalogLock)
            {
                removed = _views.Remove(viewName);
            }
            if (removed)
            {
                Save();
            }
        }

        public ViewMetadata? GetView(string viewName)
        {
            lock (_catalogLock)
            {
                return _views.TryGetValue(viewName, out var meta) ? meta : null;
            }
        }

        public bool ViewExists(string viewName)
        {
            lock (_catalogLock)
            {
                return _views.ContainsKey(viewName);
            }
        }

        public IEnumerable<ViewMetadata> ListViews()
        {
            lock (_catalogLock)
            {
                return _views.Values.ToList();
            }
        }

        // Lock Operations
        public AsyncReaderWriterLock GetTableLock(string tableName)
        {
            lock (_tableLocks)
            {
                if (!_tableLocks.TryGetValue(tableName, out var lk))
                {
                    lk = new AsyncReaderWriterLock();
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
            lock (_walInitLock)
            {
                _walWriter?.Dispose();
                _walWriter = null;
            }
            if (_ownsBufferPool)
            {
                BufferPool.Dispose();
            }
        }
    }
}
