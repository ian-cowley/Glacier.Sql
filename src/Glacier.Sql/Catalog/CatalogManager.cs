using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glacier.Polaris;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.BufferPool;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Catalog
{
    public class TableMetadata
    {
        public Guid TableId { get; set; } = Guid.NewGuid();
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
        private readonly Dictionary<Guid, TableMetadata> _tablesById = new();
        private readonly Dictionary<string, TriggerMetadata> _triggers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ViewMetadata> _views = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string DefinitionSql, SelectStatement Ast, List<ColumnMetadata>? Schema)> _viewCache = new(StringComparer.OrdinalIgnoreCase);
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

        private static Guid DeriveLegacyTableId(string tableName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((tableName ?? string.Empty).ToUpperInvariant()));
            return new Guid(hash.AsSpan(0, 16));
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
                        _tablesById.Clear();
                        if (metadataList != null)
                        {
                            foreach (var meta in metadataList)
                            {
                                if (meta.TableId == Guid.Empty)
                                {
                                    meta.TableId = DeriveLegacyTableId(meta.TableName);
                                }
                                _tables[meta.TableName] = meta;
                                _tablesById[meta.TableId] = meta;
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
                        _viewCache.Clear();
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
                if (ex is InvalidDataException)
                {
                    throw;
                }
                throw new InvalidDataException($"Failed to load catalog at '{_catalogPath}': {ex.Message}", ex);
            }
        }

        private void RecoverFromWal()
        {
            string walPath = Path.Combine(_dataDir, "glacier.wal");
            if (!File.Exists(walPath)) return;

            var records = WalReader.Read(walPath, out ulong checkpointLsn, out bool corruptHeader);
            if (corruptHeader)
            {
                throw new InvalidDataException($"Corrupt WAL header detected in '{walPath}'.");
            }
            if (records.Count == 0) return;

            var committedTx = new HashSet<Guid>();
            var flushedLsnsById = new Dictionary<Guid, ulong>();
            var flushedLsnsByName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in records)
            {
                if (r.Type == WalRecordType.TxCommit && r.TxId != Guid.Empty)
                {
                    committedTx.Add(r.TxId);
                }
                else if (r.Type == WalRecordType.CheckpointEnd && (r.TableId != Guid.Empty || !string.IsNullOrEmpty(r.TableName)))
                {
                    ulong tableLsn = r.Payload.Length >= 8
                        ? BitConverter.ToUInt64(r.Payload.AsSpan(0, 8))
                        : r.Lsn;

                    if (r.TableId != Guid.Empty)
                    {
                        if (!flushedLsnsById.TryGetValue(r.TableId, out ulong prev) || tableLsn > prev)
                        {
                            flushedLsnsById[r.TableId] = tableLsn;
                        }
                    }
                    else
                    {
                        if (!flushedLsnsByName.TryGetValue(r.TableName, out ulong prev) || tableLsn > prev)
                        {
                            flushedLsnsByName[r.TableName] = tableLsn;
                        }
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

                if (r.TableId != Guid.Empty)
                {
                    if (flushedLsnsById.TryGetValue(r.TableId, out ulong idFlushedLsn) && r.Lsn <= idFlushedLsn)
                    {
                        continue;
                    }
                }
                else if (!string.IsNullOrEmpty(r.TableName) &&
                         flushedLsnsByName.TryGetValue(r.TableName, out ulong nameFlushedLsn) &&
                         r.Lsn <= nameFlushedLsn)
                {
                    continue;
                }

                TableMetadata? meta = r.TableId != Guid.Empty ? GetTableById(r.TableId) : GetTable(r.TableName);
                if (meta == null)
                {
                    continue;
                }

                if (r.Type == WalRecordType.RowInsert)
                {
                    var entry = BufferPool.GetOrLoad(meta);
                    var insertedDf = TableStorage.DeserializeDataFrame(r.Payload);
                    if (insertedDf.Columns.Count > 0)
                    {
                        using (entry.AcquireWriteLock())
                        {
                            if (entry.CurrentSnapshot.Columns.Count == 0)
                            {
                                entry.ApplyMutation(insertedDf, r.Lsn, Guid.Empty);
                            }
                            else
                            {
                                entry.ApplyAppend(insertedDf, r.Lsn, Guid.Empty);
                            }
                        }
                        replayedCount++;
                    }
                }
                else if (r.Type == WalRecordType.RowUpdate || r.Type == WalRecordType.RowDelete)
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
                else if (r.Type == WalRecordType.RowDeleteDelta)
                {
                    var entry = BufferPool.GetOrLoad(meta);
                    using (entry.AcquireWriteLock())
                    {
                        if (r.Payload.Length >= 4)
                        {
                            int count = BitConverter.ToInt32(r.Payload.AsSpan(0, 4));
                            if (count < 0)
                            {
                                entry.ApplyMutation(TableStorage.CreateEmptyDataFrame(meta.Columns), r.Lsn, Guid.Empty);
                            }
                            else
                            {
                                var deletedSet = new HashSet<int>(count);
                                int offset = 4;
                                for (int i = 0; i < count && offset + 4 <= r.Payload.Length; i++, offset += 4)
                                {
                                    deletedSet.Add(BitConverter.ToInt32(r.Payload.AsSpan(offset, 4)));
                                }

                                var current = entry.CurrentSnapshot;
                                var keepIndices = new List<int>(Math.Max(0, current.RowCount - deletedSet.Count));
                                for (int rowIdx = 0; rowIdx < current.RowCount; rowIdx++)
                                {
                                    if (!deletedSet.Contains(rowIdx))
                                    {
                                        keepIndices.Add(rowIdx);
                                    }
                                }
                                var keptDf = TableStorage.TakeRows(current, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keepIndices));
                                entry.ApplyMutation(keptDf, r.Lsn, Guid.Empty);
                            }
                        }
                    }
                    replayedCount++;
                }
                else if (r.Type == WalRecordType.RowUpdateDelta)
                {
                    var entry = BufferPool.GetOrLoad(meta);
                    using (entry.AcquireWriteLock())
                    {
                        if (r.Payload.Length >= 4)
                        {
                            int count = BitConverter.ToInt32(r.Payload.AsSpan(0, 4));
                            int indicesBytes = count * 4;
                            if (count >= 0 && 4 + indicesBytes <= r.Payload.Length)
                            {
                                var updatedIndices = new int[count];
                                int offset = 4;
                                for (int i = 0; i < count; i++, offset += 4)
                                {
                                    updatedIndices[i] = BitConverter.ToInt32(r.Payload.AsSpan(offset, 4));
                                }

                                var updatedRowsDf = TableStorage.DeserializeDataFrame(r.Payload.AsSpan(offset));
                                if (updatedRowsDf.Columns.Count > 0)
                                {
                                    var patchedDf = TableStorage.ApplyUpdatedRows(entry.CurrentSnapshot, updatedIndices, updatedRowsDf);
                                    entry.ApplyMutation(patchedDf, r.Lsn, Guid.Empty);
                                }
                            }
                        }
                    }
                    replayedCount++;
                }
            }

            if (replayedCount > 0)
            {
                BufferPool.CheckpointAll(WalWriter);
                GlacierSqlDiagnostics.LogInformation($"[WAL Recovery] Successfully replayed {replayedCount} committed mutation records (previous checkpoint LSN {checkpointLsn}).");
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
                throw;
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
                TableId = src.TableId,
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

        public TableMetadata? GetTableById(Guid tableId)
        {
            if (tableId == Guid.Empty) return null;
            lock (_catalogLock)
            {
                return _tablesById.TryGetValue(tableId, out var meta) ? meta : null;
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
                TableId = Guid.NewGuid(),
                TableName = tableName,
                Columns = columns,
                BackingFile = Path.Combine(_dataDir, $"{tableName}.ipc")
            };

            TableStorage.InitializeTable(meta.BackingFile, columns);

            lock (_catalogLock)
            {
                _tables[tableName] = meta;
                _tablesById[meta.TableId] = meta;
                InvalidateViewSchemasLocked();
            }

            BufferPool.Invalidate(tableName);
            Save();
        }

        internal TableMetadata AddTableInMemory(string tableName, List<ColumnMetadata> columns)
        {
            var meta = new TableMetadata
            {
                TableId = Guid.NewGuid(),
                TableName = tableName,
                Columns = columns,
                BackingFile = Path.Combine(_dataDir, $"{tableName}.ipc")
            };

            lock (_catalogLock)
            {
                _tables[tableName] = meta;
                _tablesById[meta.TableId] = meta;
                InvalidateViewSchemasLocked();
            }

            BufferPool.Invalidate(tableName);
            BufferPool.GetOrLoad(meta);
            return meta;
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
                _tablesById.Remove(meta.TableId);
                InvalidateViewSchemasLocked();

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

            if (!string.IsNullOrEmpty(meta.BackingFile) && File.Exists(meta.BackingFile))
            {
                try
                {
                    File.Delete(meta.BackingFile);
                }
                catch (Exception ex) when (ex is not FileNotFoundException)
                {
                    GlacierSqlDiagnostics.LogWarning($"Failed to delete backing file '{meta.BackingFile}': {ex.Message}", ex);
                }
            }

            PruneTableLock(tableName);
        }

        internal void RemoveTableInMemory(
            string tableName,
            out TableMetadata? removedMeta,
            out List<TriggerMetadata> removedTriggers,
            out TableBufferEntry? detachedEntry)
        {
            removedTriggers = new List<TriggerMetadata>();
            detachedEntry = null;

            lock (_catalogLock)
            {
                if (!_tables.TryGetValue(tableName, out removedMeta))
                {
                    return;
                }

                _tables.Remove(tableName);
                _tablesById.Remove(removedMeta.TableId);
                InvalidateViewSchemasLocked();

                var trigNames = _triggers.Values
                    .Where(t => t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => t.TriggerName)
                    .ToList();

                foreach (var name in trigNames)
                {
                    if (_triggers.TryGetValue(name, out var trig))
                    {
                        removedTriggers.Add(trig);
                        _triggers.Remove(name);
                    }
                }
            }

            BufferPool.DetachEntry(tableName, out detachedEntry);
        }

        internal void RestoreTableInMemory(TableMetadata meta, List<TriggerMetadata> triggers, TableBufferEntry? entry)
        {
            lock (_catalogLock)
            {
                _tables[meta.TableName] = meta;
                _tablesById[meta.TableId] = meta;
                foreach (var trig in triggers)
                {
                    _triggers[trig.TriggerName] = trig;
                }
                InvalidateViewSchemasLocked();
            }

            if (entry != null)
            {
                BufferPool.AttachEntry(entry);
            }
        }

        internal void FinalizeTableDrop(TableMetadata meta, TableBufferEntry? detachedEntry)
        {
            detachedEntry?.Dispose();
            Save();

            if (!string.IsNullOrEmpty(meta.BackingFile) && File.Exists(meta.BackingFile))
            {
                try
                {
                    File.Delete(meta.BackingFile);
                }
                catch (Exception ex) when (ex is not FileNotFoundException)
                {
                    GlacierSqlDiagnostics.LogWarning($"Failed to delete backing file '{meta.BackingFile}': {ex.Message}", ex);
                }
            }

            PruneTableLock(meta.TableName);
        }

        internal void PruneTableLock(string tableName)
        {
            lock (_tableLocks)
            {
                _tableLocks.Remove(tableName);
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

        internal void AddTriggerInMemory(TriggerMetadata trig)
        {
            lock (_catalogLock)
            {
                _triggers[trig.TriggerName] = trig;
            }
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

        internal TriggerMetadata? RemoveTriggerInMemory(string triggerName)
        {
            lock (_catalogLock)
            {
                if (_triggers.TryGetValue(triggerName, out var trig))
                {
                    _triggers.Remove(triggerName);
                    return trig;
                }
                return null;
            }
        }

        public TriggerMetadata? GetTrigger(string triggerName)
        {
            lock (_catalogLock)
            {
                return _triggers.TryGetValue(triggerName, out var trig) ? trig : null;
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
            AddViewInMemory(viewName, definitionSql);
            Save();
        }

        internal void AddViewInMemory(string viewName, string definitionSql)
        {
            var meta = new ViewMetadata
            {
                ViewName = viewName,
                DefinitionSql = definitionSql
            };
            lock (_catalogLock)
            {
                _views[viewName] = meta;
                _viewCache.Remove(viewName);
                InvalidateViewSchemasLocked();
            }
        }

        public void RemoveView(string viewName)
        {
            if (RemoveViewInMemory(viewName) != null)
            {
                Save();
            }
        }

        internal ViewMetadata? RemoveViewInMemory(string viewName)
        {
            lock (_catalogLock)
            {
                if (_views.TryGetValue(viewName, out var meta))
                {
                    _views.Remove(viewName);
                    _viewCache.Remove(viewName);
                    InvalidateViewSchemasLocked();
                    return meta;
                }
                return null;
            }
        }

        internal void RestoreViewInMemory(ViewMetadata viewMeta)
        {
            lock (_catalogLock)
            {
                _views[viewMeta.ViewName] = viewMeta;
                _viewCache.Remove(viewMeta.ViewName);
                InvalidateViewSchemasLocked();
            }
        }

        internal void InvalidateAllViewSchemas()
        {
            lock (_catalogLock)
            {
                InvalidateViewSchemasLocked();
            }
        }

        private void InvalidateViewSchemasLocked()
        {
            if (_viewCache.Count == 0) return;
            foreach (var key in _viewCache.Keys.ToList())
            {
                var entry = _viewCache[key];
                _viewCache[key] = (entry.DefinitionSql, entry.Ast, null);
            }
        }

        internal bool TryGetCachedView(string viewName, string expectedSql, out SelectStatement? ast, out List<ColumnMetadata>? schema)
        {
            lock (_catalogLock)
            {
                if (_viewCache.TryGetValue(viewName, out var cached) &&
                    string.Equals(cached.DefinitionSql, expectedSql, StringComparison.Ordinal))
                {
                    ast = cached.Ast;
                    schema = cached.Schema;
                    return true;
                }
            }
            ast = null;
            schema = null;
            return false;
        }

        internal void CacheView(string viewName, string definitionSql, SelectStatement ast, List<ColumnMetadata>? schema)
        {
            lock (_catalogLock)
            {
                _viewCache[viewName] = (definitionSql, ast, schema);
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
