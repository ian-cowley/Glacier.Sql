using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Glacier.Sql.Catalog
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(CatalogData))]
    [JsonSerializable(typeof(List<TableMetadata>))]
    [JsonSerializable(typeof(TableMetadata))]
    [JsonSerializable(typeof(ColumnMetadata))]
    [JsonSerializable(typeof(TriggerMetadata))]
    [JsonSerializable(typeof(ViewMetadata))]
    internal partial class CatalogJsonContext : JsonSerializerContext
    {
    }
}
