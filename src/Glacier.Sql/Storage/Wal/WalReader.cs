using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Glacier.Sql.Diagnostics;

namespace Glacier.Sql.Storage.Wal
{
    public static class WalReader
    {
        private const int HeaderSize = 32;

        public static List<WalRecord> Read(string walFilePath, out ulong checkpointLsn)
        {
            return ReadCore(walFilePath, out checkpointLsn, out _, throwOnIoError: false);
        }

        public static List<WalRecord> Read(string walFilePath, out ulong checkpointLsn, out bool corruptHeader)
        {
            return ReadCore(walFilePath, out checkpointLsn, out corruptHeader, throwOnIoError: true);
        }

        private static List<WalRecord> ReadCore(string walFilePath, out ulong checkpointLsn, out bool corruptHeader, bool throwOnIoError)
        {
            checkpointLsn = 0;
            corruptHeader = false;
            var records = new List<WalRecord>();

            if (!File.Exists(walFilePath))
            {
                return records;
            }

            try
            {
                using var fs = new FileStream(walFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length == 0)
                {
                    return records;
                }

                if (fs.Length < HeaderSize)
                {
                    corruptHeader = true;
                    GlacierSqlDiagnostics.LogError($"Corrupt WAL header in '{walFilePath}': length {fs.Length} < {HeaderSize}.");
                    return records;
                }

                // 1. Read and validate 32-byte header + CRC32
                Span<byte> headerBuf = stackalloc byte[HeaderSize];
                if (!ReadExact(fs, headerBuf))
                {
                    corruptHeader = true;
                    GlacierSqlDiagnostics.LogError($"Corrupt WAL header in '{walFilePath}': truncated header.");
                    return records;
                }

                uint magic = BitConverter.ToUInt32(headerBuf.Slice(0, 4));
                if (magic != WalWriter.WalMagic)
                {
                    corruptHeader = true;
                    GlacierSqlDiagnostics.LogError($"Corrupt WAL header in '{walFilePath}': invalid magic 0x{magic:X8}.");
                    return records;
                }

                ushort version = BitConverter.ToUInt16(headerBuf.Slice(4, 2));
                if (version != 1 && version != 2)
                {
                    corruptHeader = true;
                    GlacierSqlDiagnostics.LogError($"Unsupported WAL version {version} in '{walFilePath}'.");
                    return records;
                }

                uint expectedHeaderCrc = BitConverter.ToUInt32(headerBuf.Slice(28, 4));
                uint actualHeaderCrc = Crc32.Compute(headerBuf.Slice(0, 28));
                if (actualHeaderCrc != expectedHeaderCrc)
                {
                    corruptHeader = true;
                    GlacierSqlDiagnostics.LogError($"Corrupt WAL header CRC32 in '{walFilePath}'.");
                    return records;
                }

                checkpointLsn = BitConverter.ToUInt64(headerBuf.Slice(8, 8));
                int minBodyLength = version == 2 ? (8 + 16 + 1 + 16 + 2 + 4) : (8 + 16 + 1 + 2 + 4);
                Span<byte> u32Buf = stackalloc byte[4];

                // 2. Read frames (stopping cleanly at torn tail frames)
                while (fs.Position + 4 <= fs.Length)
                {
                    if (!ReadExact(fs, u32Buf))
                    {
                        break;
                    }

                    uint frameRemainingLength = BitConverter.ToUInt32(u32Buf); // Body + CRC(4)
                    if (frameRemainingLength < 4 || fs.Position + frameRemainingLength > fs.Length)
                    {
                        break;
                    }

                    int bodyLength = (int)(frameRemainingLength - 4);
                    byte[] rentedBody = ArrayPool<byte>.Shared.Rent(bodyLength);
                    try
                    {
                        if (!ReadExact(fs, rentedBody.AsSpan(0, bodyLength)))
                        {
                            break;
                        }

                        if (!ReadExact(fs, u32Buf))
                        {
                            break;
                        }

                        uint expectedCrc = BitConverter.ToUInt32(u32Buf);
                        Span<byte> bodySpan = rentedBody.AsSpan(0, bodyLength);
                        uint actualCrc = Crc32.Compute(bodySpan);
                        if (actualCrc != expectedCrc || bodyLength < minBodyLength)
                        {
                            break;
                        }

                        int offset = 0;
                        ulong lsn = BitConverter.ToUInt64(bodySpan.Slice(offset, 8));
                        offset += 8;

                        Guid txId = new Guid(bodySpan.Slice(offset, 16));
                        offset += 16;

                        WalRecordType type = (WalRecordType)bodySpan[offset++];

                        Guid tableId = Guid.Empty;
                        if (version == 2)
                        {
                            tableId = new Guid(bodySpan.Slice(offset, 16));
                            offset += 16;
                        }

                        ushort nameLen = BitConverter.ToUInt16(bodySpan.Slice(offset, 2));
                        offset += 2;

                        if (offset + nameLen + 4 > bodyLength)
                        {
                            break;
                        }

                        string tableName = nameLen == 0
                            ? string.Empty
                            : Encoding.UTF8.GetString(bodySpan.Slice(offset, nameLen));
                        offset += nameLen;

                        uint payloadLen = BitConverter.ToUInt32(bodySpan.Slice(offset, 4));
                        offset += 4;

                        if (offset + payloadLen > bodyLength)
                        {
                            break;
                        }

                        byte[] payload = payloadLen == 0 ? Array.Empty<byte>() : new byte[payloadLen];
                        if (payloadLen > 0)
                        {
                            bodySpan.Slice(offset, (int)payloadLen).CopyTo(payload);
                        }

                        records.Add(new WalRecord(lsn, txId, type, tableId, tableName, payload));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rentedBody);
                    }
                }
            }
            catch (Exception ex)
            {
                GlacierSqlDiagnostics.LogError($"Error reading WAL '{walFilePath}': {ex.Message}", ex);
                if (throwOnIoError)
                {
                    throw;
                }
            }

            return records;
        }

        private static bool ReadExact(Stream stream, Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = stream.Read(destination.Slice(total));
                if (read == 0) return false;
                total += read;
            }
            return true;
        }
    }
}
