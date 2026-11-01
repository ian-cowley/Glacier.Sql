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

                // 1. Read and validate 32-byte header + CRC32
                byte[] headerBuf = new byte[HeaderSize];
                int totalHeaderRead = 0;
                while (totalHeaderRead < HeaderSize)
                {
                    int r = fs.Read(headerBuf, totalHeaderRead, HeaderSize - totalHeaderRead);
                    if (r == 0) return records;
                    totalHeaderRead += r;
                }

                uint magic = BitConverter.ToUInt32(headerBuf.AsSpan(0, 4));
                if (magic != WalWriter.WalMagic)
                {
                    return records;
                }

                uint expectedHeaderCrc = BitConverter.ToUInt32(headerBuf.AsSpan(28, 4));
                uint actualHeaderCrc = Crc32.Compute(headerBuf.AsSpan(0, 28));
                if (actualHeaderCrc != expectedHeaderCrc)
                {
                    return records;
                }

                checkpointLsn = BitConverter.ToUInt64(headerBuf.AsSpan(8, 8));

                using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

                // 2. Read frames
                while (fs.Position + 4 <= fs.Length)
                {
                    uint frameRemainingLength = reader.ReadUInt32(); // Body + CRC(4)
                    if (frameRemainingLength < 4 || fs.Position + frameRemainingLength > fs.Length)
                    {
                        break;
                    }

                    int bodyLength = (int)(frameRemainingLength - 4);
                    byte[] body = reader.ReadBytes(bodyLength);
                    if (body.Length != bodyLength)
                    {
                        break;
                    }

                    uint expectedCrc = reader.ReadUInt32();
                    uint actualCrc = Crc32.Compute(body);
                    if (actualCrc != expectedCrc)
                    {
                        break;
                    }

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
