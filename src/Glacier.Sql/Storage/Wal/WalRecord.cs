using System;

namespace Glacier.Sql.Storage.Wal
{
    public sealed class WalRecord
    {
        public ulong Lsn { get; init; }
        public Guid TxId { get; init; }
        public WalRecordType Type { get; init; }
        public string TableName { get; init; } = string.Empty;
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        public WalRecord() { }

        public WalRecord(ulong lsn, Guid txId, WalRecordType type, string tableName, byte[] payload)
        {
            Lsn = lsn;
            TxId = txId;
            Type = type;
            TableName = tableName;
            Payload = payload;
        }
    }
}
