using System;
using System.Collections.Generic;
using System.IO;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Storage.BufferPool;

namespace Glacier.Sql.Catalog
{
    public static class TableStorage
    {
        private static readonly TableBufferPool _globalPool = new();
        public static TableBufferPool BufferPool => _globalPool;

        public static DataFrame ReadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Table data file not found at: '{filePath}'");
            }

            return DataFrame.FromArrowIpc(filePath);
        }

        public static DataFrame ReadTable(string filePath)
        {
            if (_globalPool.TryGetByFile(filePath, out var cachedEntry) && cachedEntry != null)
            {
                using (cachedEntry.AcquireReadLock())
                {
                    return cachedEntry.CurrentSnapshot;
                }
            }

            var df = ReadFromFile(filePath);
            string tableName = Path.GetFileNameWithoutExtension(filePath);
            var entry = _globalPool.GetOrLoad(tableName, filePath, () => df);
            using (entry.AcquireWriteLock())
            {
                entry.CurrentSnapshot = df;
                entry.IsDirty = false;
            }
            return df;
        }

        public static void WriteTable(DataFrame df, string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic temporary write + move
            string tmpPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                df.ToArrowIpc(tmpPath);
                File.Move(tmpPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
            }

            // Sync with buffer pool
            string tableName = Path.GetFileNameWithoutExtension(filePath);
            var entry = _globalPool.GetOrLoad(tableName, filePath, () => df);
            using (entry.AcquireWriteLock())
            {
                entry.CurrentSnapshot = df;
                entry.IsDirty = false;
            }
        }

        public static byte[] SerializeDataFrame(DataFrame df)
        {
            using var ms = new MemoryStream();
            df.ToArrowIpc(ms);
            return ms.ToArray();
        }

        public static DataFrame DeserializeDataFrame(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return new DataFrame();
            }

            using var ms = new MemoryStream(bytes);
            return DataFrame.FromArrowIpc(ms);
        }

        public static DataFrame CreateEmptyDataFrame(List<ColumnMetadata> columns)
        {
            var seriesList = new List<ISeries>();
            foreach (var col in columns)
            {
                string dt = col.DataType.ToUpperInvariant();
                if (dt == "INT" || dt == "INTEGER")
                {
                    seriesList.Add(new Int32Series(col.Name, 0));
                }
                else if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
                {
                    seriesList.Add(new Float64Series(col.Name, 0));
                }
                else if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
                {
                    seriesList.Add(new Utf8StringSeries(col.Name, 0, 0));
                }
                else if (dt == "BIT" || dt == "BOOLEAN")
                {
                    seriesList.Add(new BooleanSeries(col.Name, 0));
                }
                else if (dt == "DATETIME" || dt == "DATE")
                {
                    seriesList.Add(new TimeSeries(col.Name, 0));
                }
                else
                {
                    throw new NotSupportedException($"Data type '{col.DataType}' for column '{col.Name}' is not supported.");
                }
            }
            return new DataFrame(seriesList);
        }

        public static void InitializeTable(string filePath, List<ColumnMetadata> columns)
        {
            var df = CreateEmptyDataFrame(columns);
            WriteTable(df, filePath);
        }
    }
}
