#nullable enable

using System;

namespace SimpleTrans.Protocol
{
    /// <summary>CRC-32（IEEE 802.3，反射多项式 0xEDB88320）。用于每个帧的完整性校验。</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
        {
            const uint poly = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>在已计算的中间值上继续累积（<paramref name="crc"/> 传入上一次的返回值）。</summary>
        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            uint c = crc;
            for (int i = 0; i < data.Length; i++)
            {
                c = (c >> 8) ^ Table[(c ^ data[i]) & 0xFF];
            }
            return c;
        }

        /// <summary>计算一段数据的 CRC32。</summary>
        public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFFu, data);

        /// <summary>开始一次跨多段累积的计算。</summary>
        public static uint Begin() => 0xFFFFFFFFu;

        /// <summary>结束一次跨多段累积的计算。</summary>
        public static uint Finish(uint accumulated) => ~accumulated;
    }
}