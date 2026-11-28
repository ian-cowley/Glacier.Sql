using System;

namespace Glacier.Sql.Storage.Wal
{
    /// <summary>
    /// Hardware-accelerated IEEE 802.3 32-bit Cyclic Redundancy Check (CRC32, polynomial 0xEDB88320).
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = GenerateTable();

        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint entry = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((entry & 1) == 1)
                        entry = (entry >> 1) ^ 0xEDB88320u;
                    else
                        entry >>= 1;
                }
                table[i] = entry;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            return System.IO.Hashing.Crc32.HashToUInt32(data);
        }

        public static uint Update(uint currentCrc, ReadOnlySpan<byte> data)
        {
            if (currentCrc == 0)
            {
                return System.IO.Hashing.Crc32.HashToUInt32(data);
            }

            uint crc = ~currentCrc;
            for (int i = 0; i < data.Length; i++)
            {
                byte index = (byte)((crc & 0xFF) ^ data[i]);
                crc = (crc >> 8) ^ Table[index];
            }
            return ~crc;
        }
    }
}
