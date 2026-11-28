using System;
using System.Buffers;
using System.IO;
using System.Text;
using Glacier.Sql.Diagnostics;

namespace Glacier.Sql.Storage.Wal
{
    public sealed class WalWriter : IDisposable
    {
        public const uint WalMagic = 0x57414C31; // "WAL1"
        public const ushort WalVersion = 2;
        private const int HeaderSize = 32;

        private readonly string _walFilePath;
        private readonly FileStream _stream;
        private readonly object _syncLock = new();
        private ushort _version = WalVersion;
        private ulong _currentLsn;
        private ulong _checkpointLsn;
        private bool _disposed;

        public ulong CurrentLsn
        {
            get { lock (_syncLock) return _currentLsn; }
        }

        public ulong CheckpointLsn
        {
            get { lock (_syncLock) return _checkpointLsn; }
        }

        public string WalFilePath => _walFilePath;

        public WalWriter(string walFilePath, ulong initialLsn = 0)
        {
            _walFilePath = walFilePath ?? throw new ArgumentNullException(nameof(walFilePath));
            _currentLsn = initialLsn;

            string? dir = Path.GetDirectoryName(walFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool exists = File.Exists(walFilePath);
            long existingLength = exists ? new FileInfo(walFilePath).Length : 0;
            bool isNew = !exists || existingLength == 0;

            if (!isNew && existingLength < HeaderSize)
            {
                string msg = $"Corrupt WAL file '{walFilePath}': length {existingLength} is smaller than {HeaderSize}-byte header.";
                GlacierSqlDiagnostics.LogError(msg);
                throw new InvalidDataException(msg);
            }

            _stream = new FileStream(walFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, FileOptions.None);

            if (isNew)
            {
                _version = WalVersion;
                WriteHeader(0);
                _stream.SetLength(HeaderSize);
                _stream.Flush(true);
            }
            else if (!ReadExistingHeaderAndScanFrames())
            {
                _stream.Dispose();
                string msg = $"Corrupt WAL header detected in '{walFilePath}'. Refusing to truncate.";
                GlacierSqlDiagnostics.LogError(msg);
                throw new InvalidDataException(msg);
            }
        }

        private bool ReadExistingHeaderAndScanFrames()
        {
            if (_stream.Length < HeaderSize)
            {
                return false;
            }

            _stream.Seek(0, SeekOrigin.Begin);
            Span<byte> headerBuf = stackalloc byte[HeaderSize];
            int totalRead = 0;
            while (totalRead < HeaderSize)
            {
                int r = _stream.Read(headerBuf.Slice(totalRead));
                if (r == 0) return false;
                totalRead += r;
            }

            uint magic = BitConverter.ToUInt32(headerBuf.Slice(0, 4));
            if (magic != WalMagic)
            {
                return false;
            }

            ushort version = BitConverter.ToUInt16(headerBuf.Slice(4, 2));
            if (version != 1 && version != 2)
            {
                return false;
            }

            uint expectedHeaderCrc = BitConverter.ToUInt32(headerBuf.Slice(28, 4));
            uint actualHeaderCrc = Crc32.Compute(headerBuf.Slice(0, 28));
            if (actualHeaderCrc != expectedHeaderCrc)
            {
                return false;
            }

            _version = version;
            _checkpointLsn = BitConverter.ToUInt64(headerBuf.Slice(8, 8));
            if (_currentLsn < _checkpointLsn)
            {
                _currentLsn = _checkpointLsn;
            }

            int minBodyLength = version == 2 ? (8 + 16 + 1 + 16 + 2 + 4) : (8 + 16 + 1 + 2 + 4);
            long lastValidPos = HeaderSize;
            long fileLen = _stream.Length;
            Span<byte> u32Buf = stackalloc byte[4];

            while (_stream.Position + 4 <= fileLen)
            {
                if (!ReadExact(_stream, u32Buf))
                {
                    break;
                }

                uint frameRemainingLength = BitConverter.ToUInt32(u32Buf);
                if (frameRemainingLength < 4 || _stream.Position + frameRemainingLength > fileLen)
                {
                    break;
                }

                int bodyLength = (int)(frameRemainingLength - 4);
                byte[] rentedBody = ArrayPool<byte>.Shared.Rent(bodyLength);
                try
                {
                    if (!ReadExact(_stream, rentedBody.AsSpan(0, bodyLength)))
                    {
                        break;
                    }

                    if (!ReadExact(_stream, u32Buf))
                    {
                        break;
                    }

                    uint expectedFrameCrc = BitConverter.ToUInt32(u32Buf);
                    Span<byte> bodySpan = rentedBody.AsSpan(0, bodyLength);
                    uint actualFrameCrc = Crc32.Compute(bodySpan);
                    if (actualFrameCrc != expectedFrameCrc || bodyLength < minBodyLength)
                    {
                        break;
                    }

                    int offset = 8 + 16 + 1 + (version == 2 ? 16 : 0);
                    ushort nameLen = BitConverter.ToUInt16(bodySpan.Slice(offset, 2));
                    offset += 2;
                    if (offset + nameLen + 4 > bodyLength)
                    {
                        break;
                    }

                    offset += nameLen;
                    uint payloadLen = BitConverter.ToUInt32(bodySpan.Slice(offset, 4));
                    offset += 4;
                    if (offset + payloadLen > bodyLength)
                    {
                        break;
                    }

                    ulong frameLsn = BitConverter.ToUInt64(bodySpan.Slice(0, 8));
                    if (frameLsn > _currentLsn)
                    {
                        _currentLsn = frameLsn;
                    }

                    lastValidPos = _stream.Position;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedBody);
                }
            }

            if (lastValidPos < fileLen)
            {
                _stream.SetLength(lastValidPos);
                _stream.Flush(true);
            }

            if (lastValidPos == HeaderSize && _version != WalVersion)
            {
                _version = WalVersion;
                WriteHeader(_checkpointLsn);
            }

            _stream.Seek(0, SeekOrigin.End);
            return true;
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

        private void WriteHeader(ulong checkpointLsn)
        {
            _checkpointLsn = checkpointLsn;
            _stream.Seek(0, SeekOrigin.Begin);

            Span<byte> headerBuf = stackalloc byte[HeaderSize];
            headerBuf.Clear();
            BitConverter.TryWriteBytes(headerBuf.Slice(0, 4), WalMagic);
            BitConverter.TryWriteBytes(headerBuf.Slice(4, 2), _version);
            BitConverter.TryWriteBytes(headerBuf.Slice(6, 2), (ushort)0); // Flags
            BitConverter.TryWriteBytes(headerBuf.Slice(8, 8), checkpointLsn);

            uint headerCrc = Crc32.Compute(headerBuf.Slice(0, 28));
            BitConverter.TryWriteBytes(headerBuf.Slice(28, 4), headerCrc);

            _stream.Write(headerBuf);
            _stream.Flush(true);
        }

        public void UpdateCheckpointLsn(ulong checkpointLsn)
        {
            lock (_syncLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WalWriter));

                if (checkpointLsn > _currentLsn)
                {
                    _currentLsn = checkpointLsn;
                }

                if (checkpointLsn >= _checkpointLsn)
                {
                    long endPos = Math.Max(_stream.Length, HeaderSize);
                    WriteHeader(checkpointLsn);
                    _stream.Seek(endPos, SeekOrigin.Begin);
                }
            }
        }

        public ulong AppendRecord(WalRecordType type, Guid txId, string tableName, ReadOnlySpan<byte> payload)
        {
            return AppendRecord(type, txId, Guid.Empty, tableName, payload);
        }

        public ulong AppendRecord(WalRecordType type, Guid txId, Guid tableId, string tableName, ReadOnlySpan<byte> payload)
        {
            lock (_syncLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WalWriter));

                ulong lsn = ++_currentLsn;
                string safeName = tableName ?? string.Empty;
                int maxNameBytes = Encoding.UTF8.GetMaxByteCount(safeName.Length);
                int fixedHeaderBytes = _version == 2 ? (8 + 16 + 1 + 16 + 2) : (8 + 16 + 1 + 2);
                int maxTotalBytes = 4 + fixedHeaderBytes + maxNameBytes + 4 + payload.Length + 4;

                byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(maxTotalBytes);
                try
                {
                    int offset = 4; // Reserve 4 bytes for FrameRemainingLength

                    BitConverter.TryWriteBytes(frameBuffer.AsSpan(offset, 8), lsn);
                    offset += 8;

                    txId.TryWriteBytes(frameBuffer.AsSpan(offset, 16));
                    offset += 16;

                    frameBuffer[offset++] = (byte)type;

                    if (_version == 2)
                    {
                        tableId.TryWriteBytes(frameBuffer.AsSpan(offset, 16));
                        offset += 16;
                    }

                    int nameLenOffset = offset;
                    offset += 2;

                    int actualNameBytes = safeName.Length == 0
                        ? 0
                        : Encoding.UTF8.GetBytes(safeName.AsSpan(), frameBuffer.AsSpan(offset, maxNameBytes));

                    if (actualNameBytes > ushort.MaxValue)
                    {
                        throw new InvalidOperationException($"Table name '{safeName}' exceeds maximum WAL name length.");
                    }

                    BitConverter.TryWriteBytes(frameBuffer.AsSpan(nameLenOffset, 2), (ushort)actualNameBytes);
                    offset += actualNameBytes;

                    BitConverter.TryWriteBytes(frameBuffer.AsSpan(offset, 4), (uint)payload.Length);
                    offset += 4;

                    if (!payload.IsEmpty)
                    {
                        payload.CopyTo(frameBuffer.AsSpan(offset, payload.Length));
                        offset += payload.Length;
                    }

                    int bodyLength = offset - 4;
                    uint frameCrc = Crc32.Compute(frameBuffer.AsSpan(4, bodyLength));

                    BitConverter.TryWriteBytes(frameBuffer.AsSpan(0, 4), (uint)(bodyLength + 4));
                    BitConverter.TryWriteBytes(frameBuffer.AsSpan(offset, 4), frameCrc);
                    int totalBytes = offset + 4;

                    _stream.Seek(0, SeekOrigin.End);
                    _stream.Write(frameBuffer, 0, totalBytes);

                    bool flushToDisk = txId == Guid.Empty ||
                                       type == WalRecordType.TxCommit ||
                                       type == WalRecordType.TxAbort ||
                                       type == WalRecordType.CheckpointEnd;
                    _stream.Flush(flushToDisk);

                    return lsn;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameBuffer);
                }
            }
        }

        public void Truncate(ulong newCheckpointLsn)
        {
            lock (_syncLock)
            {
                if (_disposed) return;
                if (newCheckpointLsn > _currentLsn)
                {
                    _currentLsn = newCheckpointLsn;
                }
                _version = WalVersion;
                WriteHeader(newCheckpointLsn);
                _stream.SetLength(HeaderSize);
                _stream.Flush(true);
            }
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    try
                    {
                        _stream.Flush(true);
                    }
                    catch (Exception ex)
                    {
                        GlacierSqlDiagnostics.LogError($"Error flushing WAL on dispose: {ex.Message}", ex);
                    }
                    _stream.Dispose();
                }
            }
        }
    }
}
