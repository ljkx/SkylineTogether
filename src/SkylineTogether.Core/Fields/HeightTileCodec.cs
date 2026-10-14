using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Hashing;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Codec and hash of one tile of the R16 terrain heightmap (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water). <c>TerrainSystem.m_Heightmap</c> is a
    /// 4096² R16_UNorm texture, 3.5 m per texel and 1/16 m per unit (R07 §1.1); after a stroke settles the host reads
    /// back its dirty 64×64 tiles (8 KiB raw) and sends the changed ones; clients write them with
    /// <c>Graphics.CopyTexture</c>. Texels are row-major as read back.
    /// <para>
    /// Encoding (deterministic): each texel is replaced by its difference to the previous texel (mod 2^16, the first
    /// against 0) — terrain is smooth, so the differences are small — zig-zag coded and split into a low-byte plane and
    /// a high-byte plane (the high plane is almost all zero, like the byte-plane filter the game applies to saved
    /// heightmaps, R07 §3), then zero-run coded and compressed:
    /// <code>u8 version (1) | varuint texelCount | body (see TileBodyCodec)</code>
    /// </para>
    /// <para>
    /// <see cref="Hash"/> is xxHash64 (seed 0) of the texels as little-endian bytes. The authority check compares tile
    /// hashes of <c>m_Heightmap</c> only, never the cascade or CPU heights, which contain GPU-rasterised roads (R07 §5.2).
    /// </para>
    /// <para>
    /// The material splatmap uses the same tile protocol (ARCHITECTURE.md#zone-cells-cell-maps-terrain-and-water): <c>TerrainMaterialSystem.m_UserSplatmap</c> is a
    /// 4096² <c>R8G8_UNorm</c> texture (<c>TerrainMaterialSystem.cs:758</c> [V]), so its texels read back as 16-bit
    /// values (R in the low byte) and go through this codec and hash unchanged.
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class HeightTileCodec
    {
        /// <summary>Format version written as the first byte.</summary>
        public const byte FormatVersion = 1;

        /// <summary>Upper bound of the texels of one tile (a 2048² region; terrain tiles have 4096).</summary>
        public const int MaxTexels = 1 << 22;

        private const string What = "height tile";

        /// <summary>Appends the encoding of <c>texels[0 .. count)</c> to <paramref name="output"/>; a null or
        /// <see cref="CompressionKind.None"/> compressor leaves the body uncompressed.</summary>
        public static void Encode(ushort[] texels, int count, ByteWriter output, ICompressor compressor)
        {
            Encode(texels, count, output, compressor, null);
        }

        /// <summary>
        /// <see cref="Encode(ushort[], int, ByteWriter, ICompressor)"/> that reports a failing compressor to
        /// <paramref name="log"/> (category <see cref="LogCategory.Fields"/>); the tile is then sent uncompressed.
        /// </summary>
        public static void Encode(ushort[] texels, int count, ByteWriter output, ICompressor compressor, ILog log)
        {
            if (texels == null) throw new ArgumentNullException(nameof(texels));
            if (output == null) throw new ArgumentNullException(nameof(output));
            CheckCount(count, texels.Length);
            int rawLength = 2 * count;
            byte[] raw = BufferPool.Rent(Math.Max(rawLength, 1));
            try
            {
                ushort previous = 0;
                for (int i = 0; i < count; i++)
                {
                    ushort texel = texels[i];
                    int delta = unchecked((short)(ushort)(texel - previous));
                    previous = texel;
                    uint zigzag = (uint)((delta << 1) ^ (delta >> 31)) & 0xFFFFu;
                    raw[i] = (byte)zigzag;
                    raw[count + i] = (byte)(zigzag >> 8);
                }
                output.WriteByte(FormatVersion);
                output.WriteVarUInt32((uint)count);
                TileBodyCodec.Write(raw, rawLength, compressor, output, log, What);
            }
            finally
            {
                BufferPool.Return(raw);
            }
        }

        /// <summary>
        /// Decodes one tile into <c>texels[0 .. count)</c>. The encoded texel count must equal
        /// <paramref name="count"/>. Throws <see cref="DecodeException"/> on any malformed input (and only then);
        /// <paramref name="texels"/> is written only after the whole body decoded successfully.
        /// </summary>
        public static void Decode(ByteReader input, ushort[] texels, int count, ICompressor compressor)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (texels == null) throw new ArgumentNullException(nameof(texels));
            CheckCount(count, texels.Length);
            byte version = input.ReadByte();
            if (version != FormatVersion)
            {
                throw new DecodeException(What + ": unknown format version " + version.ToString(CultureInfo.InvariantCulture));
            }
            uint encodedCount = input.ReadVarUInt32();
            if (encodedCount != (uint)count)
            {
                throw new DecodeException(What + ": the tile has " + encodedCount + " texels, expected " + count.ToString(CultureInfo.InvariantCulture));
            }
            int rawLength = 2 * count;
            byte[] raw = BufferPool.Rent(Math.Max(rawLength, 1));
            try
            {
                TileBodyCodec.Read(input, rawLength, compressor, raw, What);
                ushort previous = 0;
                for (int i = 0; i < count; i++)
                {
                    uint zigzag = raw[i] | ((uint)raw[count + i] << 8);
                    int delta = (int)(zigzag >> 1) ^ -(int)(zigzag & 1);
                    previous = unchecked((ushort)(previous + delta));
                    texels[i] = previous;
                }
            }
            finally
            {
                BufferPool.Return(raw);
            }
        }

        /// <summary>xxHash64 (seed 0) of <c>texels[0 .. count)</c> as little-endian bytes: the tile hash of the audits.</summary>
        public static unsafe ulong Hash(ushort[] texels, int count)
        {
            if (texels == null) throw new ArgumentNullException(nameof(texels));
            if (count < 0 || count > texels.Length) throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie in [0, texels.Length]");
            if (count > int.MaxValue / 2) throw new ArgumentOutOfRangeException(nameof(count), count, "the texels exceed int.MaxValue bytes");
            if (count == 0) return XxHash64.Hash((byte*)null, 0);
            if (BitConverter.IsLittleEndian)
            {
                fixed (ushort* p = texels)
                {
                    return XxHash64.Hash((byte*)p, count * 2);
                }
            }
            byte[] bytes = BufferPool.Rent(count * 2);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    bytes[2 * i] = (byte)texels[i];
                    bytes[2 * i + 1] = (byte)(texels[i] >> 8);
                }
                return XxHash64.Hash(bytes, 0, count * 2);
            }
            finally
            {
                BufferPool.Return(bytes);
            }
        }

        private static void CheckCount(int count, int length)
        {
            if (count < 0 || count > length) throw new ArgumentOutOfRangeException(nameof(count), count, "count must lie in [0, texels.Length]");
            if (count > MaxTexels) throw new ArgumentOutOfRangeException(nameof(count), count, "a tile has at most " + MaxTexels + " texels");
        }
    }
}
