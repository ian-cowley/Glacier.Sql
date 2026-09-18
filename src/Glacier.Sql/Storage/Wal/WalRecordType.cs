namespace Glacier.Sql.Storage.Wal
{
    public enum WalRecordType : byte
    {
        TxBegin = 0x01,
        RowInsert = 0x02,
        RowUpdate = 0x03,
        RowDelete = 0x04,
        TxCommit = 0x05,
        TxAbort = 0x06,
        CheckpointStart = 0x07,
        CheckpointEnd = 0x08
    }
}
