using System;
using System.Collections.Generic;
using System.IO;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Glacier.Polaris;
using Glacier.Polaris.Data;

namespace Glacier.Sql.Catalog
{
    public static class TableStorage
    {
        public static DataFrame ReadTable(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Table data file not found at: '{filePath}'");
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new ArrowFileReader(fs);
            var recordBatch = reader.ReadNextRecordBatch();
            if (recordBatch == null)
            {
                return new DataFrame();
            }
            return DataFrame.FromArrowRecordBatch(recordBatch);
        }

        public static void WriteTable(DataFrame df, string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            df.WriteIpc(filePath);
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
