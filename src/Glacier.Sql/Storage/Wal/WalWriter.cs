using System;
using System.IO;
using System.Text;

namespace Glacier.Sql.Storage.Wal
{
    public sealed class WalWriter : IDisposable
    {
        public const uint WalMagic = 0x57414C31; // "WAL1"
        private const int HeaderSize = 32;

        private readonly string _walFilePath;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly object _syncLock = new();
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
            _walFilePath = walFilePath;
            _currentLsn = initialLsn;

            string? dir = Path.GetDirectoryName(walFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bool isNew = !File.Exists(walFilePath) || new FileInfo(walFilePath).Length < HeaderSize;
            _stream = new FileStream(walFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 65536, FileOptions.WriteThrough);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);

            if (isNew)
            {
                WriteHeader(0);
                _stream.SetLength(HeaderSize);
                _stream.Flush(true);
            }
            else if (!ReadExistingHeaderAndScanFrames())
            {
                WriteHeader(_currentLsn);
                _stream.SetLength(HeaderSize);
                _stream.Flush(true);
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

            uint expectedHeaderCrc = BitConverter.ToUInt32(headerBuf.Slice(28, 4));
            uint actualHeaderCrc = Crc32.Compute(headerBuf.Slice(0, 28));
            if (actualHeaderCrc != expectedHeaderCrc)
            {
                return false;
            }

            _checkpointLsn = BitConverter.ToUInt64(headerBuf.Slice(8, 8));
            if (_currentLsn < _checkpointLsn)
            {
                _currentLsn = _checkpointLsn;
            }

            // Scan existing frames to restore monotonic maxFrameLsn and trim any torn tail
            using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            long lastValidPos = HeaderSize;
            long fileLen = _stream.Length;

            while (_stream.Position + 4 <= fileLen)
            {
                uint frameRemainingLength = reader.ReadUInt32();
                if (frameRemainingLength < 4 || _stream.Position + frameRemainingLength > fileLen)
                {
                    break;
                }

                int bodyLength = (int)(frameRemainingLength - 4);
                byte[] body = reader.ReadBytes(bodyLength);
                if (body.Length != bodyLength)
                {
                    break;
                }

                uint expectedFrameCrc = reader.ReadUInt32();
                uint actualFrameCrc = Crc32.Compute(body);
                if (actualFrameCrc != expectedFrameCrc || body.Length < 8 + 16 + 1 + 2 + 4)
                {
                    break;
                }

                int offset = 8 + 16 + 1;
                ushort nameLen = BitConverter.ToUInt16(body.AsSpan(offset, 2));
                offset += 2;
                if (offset + nameLen + 4 > body.Length)
                {
                    break;
                }

                offset += nameLen;
                uint payloadLen = BitConverter.ToUInt32(body.AsSpan(offset, 4));
                offset += 4;
                if (offset + payloadLen > body.Length)
                {
                    break;
                }

                ulong frameLsn = BitConverter.ToUInt64(body.AsSpan(0, 8));
                if (frameLsn > _currentLsn)
                {
                    _currentLsn = frameLsn;
                }

                lastValidPos = _stream.Position;
            }

            if (lastValidPos < fileLen)
            {
                _stream.SetLength(lastValidPos);
                _stream.Flush(true);
            }

            _stream.Seek(0, SeekOrigin.End);
            return true;
        }

        private void WriteHeader(ulong checkpointLsn)
        {
            _checkpointLsn = checkpointLsn;
            _stream.Seek(0, SeekOrigin.Begin);

            Span<byte> headerBuf = stackalloc byte[HeaderSize];
            headerBuf.Clear();
            BitConverter.TryWriteBytes(headerBuf.Slice(0, 4), WalMagic);
            BitConverter.TryWriteBytes(headerBuf.Slice(4, 2), (ushort)1); // Version
            BitConverter.TryWriteBytes(headerBuf.Slice(6, 2), (ushort)0); // Flags
            BitConverter.TryWriteBytes(headerBuf.Slice(8, 8), checkpointLsn); // CheckpointLSN

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
            lock (_syncLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WalWriter));

                ulong lsn = ++_currentLsn;
                byte[] nameBytes = Encoding.UTF8.GetBytes(tableName ?? string.Empty);

                // Body = LSN(8) + TxId(16) + Type(1) + NameLen(2) + Name(N) + PayloadLen(4) + Payload(M)
                int bodyLength = 8 + 16 + 1 + 2 + nameBytes.Length + 4 + payload.Length;
                byte[] frameBody = new byte[bodyLength];
                int offset = 0;

                BitConverter.TryWriteBytes(frameBody.AsSpan(offset, 8), lsn);
                offset += 8;

                txId.TryWriteBytes(frameBody.AsSpan(offset, 16));
                offset += 16;

                frameBody[offset++] = (byte)type;

                BitConverter.TryWriteBytes(frameBody.AsSpan(offset, 2), (ushort)nameBytes.Length);
                offset += 2;

                nameBytes.CopyTo(frameBody.AsSpan(offset, nameBytes.Length));
                offset += nameBytes.Length;

                BitConverter.TryWriteBytes(frameBody.AsSpan(offset, 4), (uint)payload.Length);
                offset += 4;

                if (!payload.IsEmpty)
                {
                    payload.CopyTo(frameBody.AsSpan(offset, payload.Length));
                }

                uint frameCrc = Crc32.Compute(frameBody);

                // Write to stream: Length(4) + Body(N) + CRC32(4)
                _stream.Seek(0, SeekOrigin.End);
                _writer.Write((uint)(bodyLength + 4));
                _writer.Write(frameBody);
                _writer.Write(frameCrc);
                _stream.Flush(true);

                return lsn;
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
                    try { _stream.Flush(true); } catch { }
                    _writer.Dispose();
                    _stream.Dispose();
                }
            }
        }
    }
}
