using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Protocol
{
    /// <summary>
    /// Run-length coding of the zone of every cell of a zone block (the payload of a ZONE record, docs/history/ARCHITECTURE-original.md §4.10.7). The
    /// values are opaque <c>ushort</c>s chosen by the caller (zone prefab ranks, never the machine-local
    /// <c>ZoneType.m_Index</c>, docs/history/ARCHITECTURE-original.md §3.5); a vanilla block has at most 10 × 6 cells (<c>ZoneUtils.MAX_ZONE_WIDTH</c>,
    /// <c>MAX_ZONE_DEPTH</c>; <c>Game.Zones/BlockSystem.cs</c> creates blocks of up to 10 cells by 6).
    /// <code>
    /// varint cellCount | repeat { varint runLength (≥ 1) | varint value (≤ 65535) } until the runs cover cellCount cells
    /// </code>
    /// Adjacent runs have different values (the encoder merges equal neighbours, the decoder rejects anything else),
    /// so every cell pattern has exactly one encoding.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class ZoneOverlayCodec
    {
        /// <summary>Largest cell count accepted (far above vanilla's 60, room for mods with larger blocks).</summary>
        public const int MaxCells = 65536;

        /// <summary>Appends the coding of <c>zoneRanks[0 .. cellCount-1]</c>.</summary>
        public static void Encode(ushort[] zoneRanks, int cellCount, ByteWriter output)
        {
            if (zoneRanks == null) throw new ArgumentNullException(nameof(zoneRanks));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (cellCount < 0 || cellCount > zoneRanks.Length || cellCount > MaxCells)
            {
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "0 ... min(zoneRanks.Length, " + MaxCells.ToString(CultureInfo.InvariantCulture) + ")");
            }
            output.WriteVarUInt32((uint)cellCount);
            int i = 0;
            while (i < cellCount)
            {
                ushort value = zoneRanks[i];
                int run = 1;
                while (i + run < cellCount && zoneRanks[i + run] == value) run++;
                output.WriteVarUInt32((uint)run);
                output.WriteVarUInt32(value);
                i += run;
            }
        }

        /// <summary>
        /// Decodes into <paramref name="zoneRanks"/> and returns the cell count. Throws <see cref="DecodeException"/>
        /// when the count exceeds the array or <see cref="MaxCells"/>, a run is empty or overshoots the count, a value
        /// exceeds 65535, or two adjacent runs share a value. Cells are written while decoding, so after a failure the
        /// array holds a partial result: decode into a scratch array and copy to live state only on success.
        /// </summary>
        public static int Decode(ByteReader input, ushort[] zoneRanks)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (zoneRanks == null) throw new ArgumentNullException(nameof(zoneRanks));
            uint count = input.ReadVarUInt32();
            if (count > MaxCells || count > (uint)zoneRanks.Length)
            {
                throw new DecodeException("zone overlay: " + WireFormat.Format(count) + " cells exceed the block ("
                                          + Math.Min(zoneRanks.Length, MaxCells).ToString(CultureInfo.InvariantCulture) + ")");
            }
            int cells = (int)count;
            int filled = 0;
            int previous = -1;
            while (filled < cells)
            {
                uint run = input.ReadVarUInt32();
                if (run == 0 || run > (uint)(cells - filled))
                {
                    throw new DecodeException("zone overlay: run of " + WireFormat.Format(run) + " cells where "
                                              + (cells - filled).ToString(CultureInfo.InvariantCulture) + " remain");
                }
                uint value = input.ReadVarUInt32();
                if (value > ushort.MaxValue) throw new DecodeException("zone overlay: value " + WireFormat.Format(value) + " exceeds 65535");
                if ((int)value == previous) throw new DecodeException("zone overlay: adjacent runs with the same value (non-canonical)");
                for (int i = 0; i < (int)run; i++) zoneRanks[filled + i] = (ushort)value;
                filled += (int)run;
                previous = (int)value;
            }
            return cells;
        }
    }
}
