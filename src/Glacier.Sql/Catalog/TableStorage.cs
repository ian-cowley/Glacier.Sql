using System;
using System.Collections.Generic;
using System.IO;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Storage.BufferPool;

namespace Glacier.Sql.Catalog
{
    public static class TableStorage
    {
        public static DataFrame ReadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Table data file not found at: '{filePath}'");
            }

            return DataFrame.FromArrowIpc(filePath);
        }

        public static DataFrame ReadTable(string filePath, TableBufferPool? bufferPool = null)
        {
            if (bufferPool != null && bufferPool.TryGetByFile(filePath, out var cachedEntry) && cachedEntry != null)
            {
                using (cachedEntry.AcquireReadLock())
                {
                    return cachedEntry.CurrentSnapshot;
                }
            }

            return ReadFromFile(filePath);
        }

        public static void WriteTable(DataFrame df, string filePath, TableBufferPool? bufferPool = null)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

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

            if (bufferPool != null)
            {
                string tableName = Path.GetFileNameWithoutExtension(filePath);
                var entry = bufferPool.GetOrLoad(tableName, filePath, () => df);
                using (entry.AcquireWriteLock())
                {
                    entry.CurrentSnapshot = df;
                    entry.CommittedSnapshot = df;
                    entry.HasUnflushedCommittedChanges = false;
                    entry.FirstUnflushedLsn = 0;
                    entry.IsDirty = false;
                }
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

        public static DataFrame DeserializeDataFrame(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return new DataFrame();
            }

            using var ms = new MemoryStream(bytes.ToArray());
            return DataFrame.FromArrowIpc(ms);
        }

        public static DataFrame TakeRows(DataFrame source, ReadOnlySpan<int> indices)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            if (indices.IsEmpty)
            {
                var emptyCols = new List<ISeries>(source.Columns.Count);
                foreach (var col in source.Columns)
                {
                    emptyCols.Add(col.CloneEmpty(0));
                }
                return new DataFrame(emptyCols);
            }

            var takenCols = new List<ISeries>(source.Columns.Count);
            foreach (var col in source.Columns)
            {
                if (col is Utf8StringSeries u8)
                {
                    long totalBytes = 0;
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int idx = indices[i];
                        if (idx >= 0 && u8.ValidityMask.IsValid(idx))
                        {
                            totalBytes += u8.GetStringSpan(idx).Length;
                        }
                    }

                    var target = new Utf8StringSeries(u8.Name, indices.Length, checked((int)totalBytes));
                    u8.Take(target, indices);
                    takenCols.Add(target);
                }
                else
                {
                    var target = col.CloneEmpty(indices.Length);
                    col.Take(target, indices);
                    takenCols.Add(target);
                }
            }

            return new DataFrame(takenCols);
        }

        public static DataFrame ApplyUpdatedRows(DataFrame baseDf, ReadOnlySpan<int> updatedIndices, DataFrame updatedRowsDf)
        {
            if (baseDf == null) throw new ArgumentNullException(nameof(baseDf));
            if (updatedRowsDf == null) throw new ArgumentNullException(nameof(updatedRowsDf));

            int totalRows = baseDf.RowCount;
            if (updatedIndices.IsEmpty || totalRows == 0)
            {
                return baseDf;
            }

            var rowToUpdateIdx = new int[totalRows];
            Array.Fill(rowToUpdateIdx, -1);
            for (int k = 0; k < updatedIndices.Length; k++)
            {
                int rowIdx = updatedIndices[k];
                if ((uint)rowIdx < (uint)totalRows)
                {
                    rowToUpdateIdx[rowIdx] = k;
                }
            }

            var resultCols = new List<ISeries>(baseDf.Columns.Count);
            for (int c = 0; c < baseDf.Columns.Count; c++)
            {
                var srcCol = baseDf.Columns[c];
                var updCol = updatedRowsDf.Columns[c];

                if (srcCol is Int32Series s32 && updCol is Int32Series u32)
                {
                    var dst = new Int32Series(s32.Name, totalRows);
                    s32.Memory.Span.CopyTo(dst.Memory.Span);
                    s32.ValidityMask.CopyTo(dst.ValidityMask, 0);
                    for (int k = 0; k < updatedIndices.Length; k++)
                    {
                        int r = updatedIndices[k];
                        if ((uint)r >= (uint)totalRows) continue;
                        if (u32.ValidityMask.IsValid(k))
                        {
                            dst.Memory.Span[r] = u32.Memory.Span[k];
                            dst.ValidityMask.SetValid(r);
                        }
                        else
                        {
                            dst.ValidityMask.SetNull(r);
                        }
                    }
                    resultCols.Add(dst);
                }
                else if (srcCol is Float64Series sf64 && updCol is Float64Series uf64)
                {
                    var dst = new Float64Series(sf64.Name, totalRows);
                    sf64.Memory.Span.CopyTo(dst.Memory.Span);
                    sf64.ValidityMask.CopyTo(dst.ValidityMask, 0);
                    for (int k = 0; k < updatedIndices.Length; k++)
                    {
                        int r = updatedIndices[k];
                        if ((uint)r >= (uint)totalRows) continue;
                        if (uf64.ValidityMask.IsValid(k))
                        {
                            dst.Memory.Span[r] = uf64.Memory.Span[k];
                            dst.ValidityMask.SetValid(r);
                        }
                        else
                        {
                            dst.ValidityMask.SetNull(r);
                        }
                    }
                    resultCols.Add(dst);
                }
                else if (srcCol is BooleanSeries sb && updCol is BooleanSeries ub)
                {
                    var dst = new BooleanSeries(sb.Name, totalRows);
                    sb.Memory.Span.CopyTo(dst.Memory.Span);
                    sb.ValidityMask.CopyTo(dst.ValidityMask, 0);
                    for (int k = 0; k < updatedIndices.Length; k++)
                    {
                        int r = updatedIndices[k];
                        if ((uint)r >= (uint)totalRows) continue;
                        if (ub.ValidityMask.IsValid(k))
                        {
                            dst.Memory.Span[r] = ub.Memory.Span[k];
                            dst.ValidityMask.SetValid(r);
                        }
                        else
                        {
                            dst.ValidityMask.SetNull(r);
                        }
                    }
                    resultCols.Add(dst);
                }
                else if (srcCol is TimeSeries st && updCol is TimeSeries ut)
                {
                    var dst = new TimeSeries(st.Name, totalRows);
                    st.Memory.Span.CopyTo(dst.Memory.Span);
                    st.ValidityMask.CopyTo(dst.ValidityMask, 0);
                    for (int k = 0; k < updatedIndices.Length; k++)
                    {
                        int r = updatedIndices[k];
                        if ((uint)r >= (uint)totalRows) continue;
                        if (ut.ValidityMask.IsValid(k))
                        {
                            dst.Memory.Span[r] = ut.Memory.Span[k];
                            dst.ValidityMask.SetValid(r);
                        }
                        else
                        {
                            dst.ValidityMask.SetNull(r);
                        }
                    }
                    resultCols.Add(dst);
                }
                else if (srcCol is Utf8StringSeries su8 && updCol is Utf8StringSeries uu8)
                {
                    long totalBytes = 0;
                    for (int r = 0; r < totalRows; r++)
                    {
                        int k = rowToUpdateIdx[r];
                        if (k >= 0)
                        {
                            if (uu8.ValidityMask.IsValid(k)) totalBytes += uu8.GetStringSpan(k).Length;
                        }
                        else
                        {
                            if (su8.ValidityMask.IsValid(r)) totalBytes += su8.GetStringSpan(r).Length;
                        }
                    }

                    var dst = new Utf8StringSeries(su8.Name, totalRows, checked((int)totalBytes));
                    var dstData = dst.DataBytes.Span;
                    var dstOffsets = dst.Offsets.Span;
                    int curOffset = 0;
                    for (int r = 0; r < totalRows; r++)
                    {
                        dstOffsets[r] = curOffset;
                        int k = rowToUpdateIdx[r];
                        if (k >= 0)
                        {
                            if (!uu8.ValidityMask.IsValid(k))
                            {
                                dst.ValidityMask.SetNull(r);
                            }
                            else
                            {
                                var span = uu8.GetStringSpan(k);
                                span.CopyTo(dstData.Slice(curOffset));
                                curOffset += span.Length;
                                dst.ValidityMask.SetValid(r);
                            }
                        }
                        else
                        {
                            if (!su8.ValidityMask.IsValid(r))
                            {
                                dst.ValidityMask.SetNull(r);
                            }
                            else
                            {
                                var span = su8.GetStringSpan(r);
                                span.CopyTo(dstData.Slice(curOffset));
                                curOffset += span.Length;
                                dst.ValidityMask.SetValid(r);
                            }
                        }
                    }
                    dstOffsets[totalRows] = curOffset;
                    resultCols.Add(dst);
                }
                else
                {
                    var dst = srcCol.CloneEmpty(totalRows);
                    for (int r = 0; r < totalRows; r++)
                    {
                        int k = rowToUpdateIdx[r];
                        if (k >= 0)
                        {
                            updCol.Take(dst, k, r);
                        }
                        else
                        {
                            srcCol.Take(dst, r, r);
                        }
                    }
                    resultCols.Add(dst);
                }
            }

            return new DataFrame(resultCols);
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

        public static void InitializeTable(string filePath, List<ColumnMetadata> columns, TableBufferPool? bufferPool = null)
        {
            var df = CreateEmptyDataFrame(columns);
            WriteTable(df, filePath, bufferPool);
        }

        internal static void LogWarningBestEffort(string message, Exception ex)
        {
            GlacierSqlDiagnostics.LogWarning(message, ex);
        }
    }
}
