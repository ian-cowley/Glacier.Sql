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

        public ulong CurrentLsn => _currentLsn;
        public ulong CheckpointLsn => _checkpointLsn;
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
            }
            else
            {
                ReadExistingHeader();
                _stream.Seek(0, SeekOrigin.End);
            }
        }

        private void ReadExistingHeader()
        {
            _stream.Seek(0, SeekOrigin.Begin);
            using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            uint magic = reader.ReadUInt32();
            if (magic == WalMagic)
            {
                ushort version = reader.ReadUInt16();
                ushort flags = reader.ReadUInt16();
                _checkpointLsn = reader.ReadUInt64();
                if (_currentLsn < _checkpointLsn)
                {
                    _currentLsn = _checkpointLsn;
                }
            }
        }

        private void WriteHeader(ulong checkpointLsn)
        {
            _checkpointLsn = checkpointLsn;
            _stream.Seek(0, SeekOrigin.Begin);

            byte[] headerBuf = new byte[HeaderSize];
            BitConverter.TryWriteBytes(headerBuf.AsSpan(0, 4), WalMagic);
            BitConverter.TryWriteBytes(headerBuf.AsSpan(4, 2), (ushort)1); // Version
            BitConverter.TryWriteBytes(headerBuf.AsSpan(6, 2), (ushort)0); // Flags
            BitConverter.TryWriteBytes(headerBuf.AsSpan(8, 8), checkpointLsn); // CheckpointLSN
            // 16..27 are 12 reserved bytes (zeroed)

            uint headerCrc = Crc32.Compute(headerBuf.AsSpan(0, 28));
            BitConverter.TryWriteBytes(headerBuf.AsSpan(28, 4), headerCrc);

            _writer.Write(headerBuf);
            _stream.Flush(true);
        }

        public void UpdateCheckpointLsn(ulong checkpointLsn)
        {
            lock (_syncLock)
            {
                long currentPos = _stream.Position;
                WriteHeader(checkpointLsn);
                _stream.Seek(currentPos, SeekOrigin.Begin);
            }
        }

        public ulong AppendRecord(WalRecordType type, Guid txId, string tableName, ReadOnlySpan<byte> payload)
        {
            lock (_syncLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(WalWriter));

                ulong lsn = ++_currentLsn;
                byte[] nameBytes = Encoding.UTF8.GetBytes(tableName);

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
                    offset += payload.Length;
                }

                uint frameCrc = Crc32.Compute(frameBody);

                // Write to stream: Length(4) + Body(N) + CRC32(4)
                _stream.Seek(0, SeekOrigin.End);
                _writer.Write((uint)(bodyLength + 4)); // Total frame payload after length field
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
                _currentLsn = newCheckpointLsn;
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
