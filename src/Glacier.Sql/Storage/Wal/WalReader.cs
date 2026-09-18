using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Glacier.Sql.Storage.Wal
{
    public static class WalReader
    {
        private const int HeaderSize = 32;

        public static List<WalRecord> Read(string walFilePath, out ulong checkpointLsn)
        {
            checkpointLsn = 0;
            var records = new List<WalRecord>();

            if (!File.Exists(walFilePath))
            {
                return records;
            }

            try
            {
                using var fs = new FileStream(walFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < HeaderSize)
                {
                    return records;
                }

                using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

                // 1. Read and validate header
                uint magic = reader.ReadUInt32();
                if (magic != WalWriter.WalMagic)
                {
                    return records;
                }

                ushort version = reader.ReadUInt16();
                ushort flags = reader.ReadUInt16();
                checkpointLsn = reader.ReadUInt64();

                byte[] reserved = reader.ReadBytes(12);
                uint headerCrc = reader.ReadUInt32();

                // 2. Read frames
                while (fs.Position + 4 <= fs.Length)
                {
                    uint frameRemainingLength = reader.ReadUInt32(); // Body + CRC(4)
                    if (frameRemainingLength < 4 || fs.Position + frameRemainingLength > fs.Length)
                    {
                        // Incomplete frame at EOF (e.g. crash during write)
                        break;
                    }

                    int bodyLength = (int)(frameRemainingLength - 4);
                    byte[] body = reader.ReadBytes(bodyLength);
                    uint expectedCrc = reader.ReadUInt32();

                    uint actualCrc = Crc32.Compute(body);
                    if (actualCrc != expectedCrc)
                    {
                        // Corrupted frame detected, stop reading
                        break;
                    }

                    // Parse frame body
                    // LSN(8) + TxId(16) + Type(1) + NameLen(2) + Name(N) + PayloadLen(4) + Payload(M)
                    if (body.Length < 8 + 16 + 1 + 2 + 4)
                    {
                        break;
                    }

                    int offset = 0;
                    ulong lsn = BitConverter.ToUInt64(body.AsSpan(offset, 8));
                    offset += 8;

                    Guid txId = new Guid(body.AsSpan(offset, 16));
                    offset += 16;

                    WalRecordType type = (WalRecordType)body[offset++];

                    ushort nameLen = BitConverter.ToUInt16(body.AsSpan(offset, 2));
                    offset += 2;

                    if (offset + nameLen + 4 > body.Length)
                    {
                        break;
                    }

                    string tableName = Encoding.UTF8.GetString(body.AsSpan(offset, nameLen));
                    offset += nameLen;

                    uint payloadLen = BitConverter.ToUInt32(body.AsSpan(offset, 4));
                    offset += 4;

                    if (offset + payloadLen > body.Length)
                    {
                        break;
                    }

                    byte[] payload = new byte[payloadLen];
                    if (payloadLen > 0)
                    {
                        Buffer.BlockCopy(body, offset, payload, 0, (int)payloadLen);
                    }

                    records.Add(new WalRecord(lsn, txId, type, tableName, payload));
                }
            }
            catch
            {
                // Return records parsed up to the point of any I/O failure
            }

            return records;
        }
    }
}
