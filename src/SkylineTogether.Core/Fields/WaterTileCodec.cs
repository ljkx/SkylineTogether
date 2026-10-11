using System;
using System.Globalization;
using SkylineTogether.Core.Buffers;

namespace SkylineTogether.Core.Fields
{
    /// <summary>
    /// Quantisation and codec of one water correction tile (ARCH §4.10.10, R07 §6.7). The host reads a region of
    /// <c>WaterSystem.WaterTexture</c> (RGBA32F per texel: x = depth in metres, y/z = velocity, w = pollution fraction;
    /// <c>SurfaceWater.cs</c>, <c>BaseDataReader.cs:108-112</c> [V]: the depth scale is 1, so depths are metres) and
    /// clients write the decoded floats back with <c>Graphics.CopyTexture</c>. Water affects visuals only (gameplay
    /// that reads water runs on the host), so the quantisation is chosen for appearance:
    /// <list type="bullet">
    /// <item>depth: signed millimetres (error ≤ 0.5 mm). The design names u16 millimetres, but sea water is often deeper
    /// than 65.535 m (default sea level 511.7 m, R07 §6.1), so the millimetres are kept as 32-bit integers; the
    /// difference-filtered byte planes cost no more than u16 where the water is shallow.</item>
    /// <item>pollution: <c>round(clamp(p, 0, 1) × 255)</c> (error ≤ 1/510 inside [0, 1]).</item>
    /// <item>velocity (only <see cref="WaterTileLevel.DepthPollutionVelocity"/>): two IEEE half floats; with
    /// <see cref="WaterTileLevel.DepthPollution"/> the decoder writes zero velocity, which the client simulation
    /// re-derives.</item>
    /// </list>
    /// NaN depth or pollution is sent as 0; non-finite velocities as 0, magnitudes above 65504 are clamped.
    /// <para>
    /// Layout before the body coding (texel order as given, n texels): four planes of the zig-zag coded differences of
    /// consecutive depth values (bytes 0-3), one plane of pollution differences (mod 256), and with velocity four planes
    /// of zig-zag coded differences of the half-float bit patterns (x low, x high, y low, y high). Wire format:
    /// <code>u8 version (1) | u8 level | varuint texelCount | body (see TileBodyCodec)</code>
    /// </para>
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class WaterTileCodec
    {
        /// <summary>Format version written as the first byte.</summary>
        public const byte FormatVersion = 1;

        /// <summary>Floats per texel in the <c>rgba</c> arrays (RGBA32F).</summary>
        public const int ChannelsPerTexel = 4;

        /// <summary>Upper bound of the texels of one tile (a 2048² region; tiles have 256² = 65536).</summary>
        public const int MaxTexels = 1 << 22;

        /// <summary>Depths are carried in steps of one millimetre.</summary>
        public const double DepthStepMetres = 0.001;

        /// <summary>Largest depth magnitude in millimetres (about 67 km); larger values are clamped.</summary>
        public const int MaxDepthMillimetres = 1 << 26;

        /// <summary>Pollution is carried as one of 256 levels over [0, 1].</summary>
        public const int PollutionSteps = 255;

        private const double MillimetresPerMetre = 1000.0;

        private const string What = "water tile";

        /// <summary>Appends the encoding of <c>rgba[0 .. 4 × texelCount)</c> to <paramref name="output"/>.</summary>
        public static void Encode(float[] rgba, int texelCount, WaterTileLevel level, ByteWriter output, ICompressor compressor)
        {
            Encode(rgba, texelCount, level, output, compressor, null);
        }

        /// <summary>
        /// <see cref="Encode(float[], int, WaterTileLevel, ByteWriter, ICompressor)"/> that reports a failing compressor
        /// to <paramref name="log"/> (category <see cref="LogCategory.Fields"/>); the tile is then sent uncompressed.
        /// </summary>
        public static void Encode(float[] rgba, int texelCount, WaterTileLevel level, ByteWriter output, ICompressor compressor, ILog log)
        {
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            if (output == null) throw new ArgumentNullException(nameof(output));
            CheckCount(texelCount, rgba.Length);
            if (level != WaterTileLevel.DepthPollution && level != WaterTileLevel.DepthPollutionVelocity)
            {
                throw new ArgumentOutOfRangeException(nameof(level), level, "unknown water tile level");
            }
            int n = texelCount;
            bool velocity = level == WaterTileLevel.DepthPollutionVelocity;
            int rawLength = RawLength(n, velocity);
            byte[] raw = BufferPool.Rent(Math.Max(rawLength, 1));
            try
            {
                int previousDepth = 0;
                byte previousPollution = 0;
                ushort previousX = 0, previousY = 0;
                for (int i = 0; i < n; i++)
                {
                    int t = i * ChannelsPerTexel;
                    int depth = QuantizeDepthMillimetres(rgba[t]);
                    int depthDelta = unchecked(depth - previousDepth);
                    previousDepth = depth;
                    uint zigzag = unchecked((uint)((depthDelta << 1) ^ (depthDelta >> 31)));
                    raw[i] = (byte)zigzag;
                    raw[n + i] = (byte)(zigzag >> 8);
                    raw[2 * n + i] = (byte)(zigzag >> 16);
                    raw[3 * n + i] = (byte)(zigzag >> 24);

                    byte pollution = QuantizePollution(rgba[t + 3]);
                    raw[4 * n + i] = unchecked((byte)(pollution - previousPollution));
                    previousPollution = pollution;

                    if (velocity)
                    {
                        ushort x = QuantizeVelocity(rgba[t + 1]);
                        ushort y = QuantizeVelocity(rgba[t + 2]);
                        uint zx = ZigZag16(unchecked((short)(ushort)(x - previousX)));
                        uint zy = ZigZag16(unchecked((short)(ushort)(y - previousY)));
                        previousX = x;
                        previousY = y;
                        raw[5 * n + i] = (byte)zx;
                        raw[6 * n + i] = (byte)(zx >> 8);
                        raw[7 * n + i] = (byte)zy;
                        raw[8 * n + i] = (byte)(zy >> 8);
                    }
                }
                output.WriteByte(FormatVersion);
                output.WriteByte((byte)level);
                output.WriteVarUInt32((uint)n);
                TileBodyCodec.Write(raw, rawLength, compressor, output, log, What);
            }
            finally
            {
                BufferPool.Return(raw);
            }
        }

        /// <summary>
        /// Decodes one tile into <c>rgba[0 .. 4 × texelCount)</c> and returns the level it was encoded with. The encoded
        /// texel count must equal <paramref name="texelCount"/>. Throws <see cref="DecodeException"/> on any malformed
        /// input (including depths outside ±<see cref="MaxDepthMillimetres"/> and non-finite velocities);
        /// <paramref name="rgba"/> is written only after the whole tile was validated.
        /// </summary>
        public static WaterTileLevel Decode(ByteReader input, float[] rgba, int texelCount, ICompressor compressor)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (rgba == null) throw new ArgumentNullException(nameof(rgba));
            CheckCount(texelCount, rgba.Length);
            byte version = input.ReadByte();
            if (version != FormatVersion)
            {
                throw new DecodeException(What + ": unknown format version " + version.ToString(CultureInfo.InvariantCulture));
            }
            byte levelByte = input.ReadByte();
            if (levelByte > (byte)WaterTileLevel.DepthPollutionVelocity)
            {
                throw new DecodeException(What + ": unknown level " + levelByte.ToString(CultureInfo.InvariantCulture));
            }
            var level = (WaterTileLevel)levelByte;
            uint encodedCount = input.ReadVarUInt32();
            if (encodedCount != (uint)texelCount)
            {
                throw new DecodeException(What + ": the tile has " + encodedCount + " texels, expected " + texelCount.ToString(CultureInfo.InvariantCulture));
            }
            int n = texelCount;
            bool velocity = level == WaterTileLevel.DepthPollutionVelocity;
            int rawLength = RawLength(n, velocity);
            byte[] raw = BufferPool.Rent(Math.Max(rawLength, 1));
            try
            {
                TileBodyCodec.Read(input, rawLength, compressor, raw, What);
                Validate(raw, n, velocity);
                int depth = 0;
                byte pollution = 0;
                ushort x = 0, y = 0;
                for (int i = 0; i < n; i++)
                {
                    int t = i * ChannelsPerTexel;
                    depth = unchecked(depth + UnZigZag32(ReadPlanes32(raw, n, i)));
                    pollution = unchecked((byte)(pollution + raw[4 * n + i]));
                    rgba[t] = (float)(depth / MillimetresPerMetre);
                    rgba[t + 3] = pollution / (float)PollutionSteps;
                    if (velocity)
                    {
                        x = unchecked((ushort)(x + UnZigZag16((uint)(raw[5 * n + i] | (raw[6 * n + i] << 8)))));
                        y = unchecked((ushort)(y + UnZigZag16((uint)(raw[7 * n + i] | (raw[8 * n + i] << 8)))));
                        rgba[t + 1] = HalfFloat.ToSingle(x);
                        rgba[t + 2] = HalfFloat.ToSingle(y);
                    }
                    else
                    {
                        rgba[t + 1] = 0f;
                        rgba[t + 2] = 0f;
                    }
                }
                return level;
            }
            finally
            {
                BufferPool.Return(raw);
            }
        }

        /// <summary>The depth (metres) a client receives for <paramref name="depth"/>: the quantisation round trip.</summary>
        public static float QuantizeDepth(float depth) => (float)(QuantizeDepthMillimetres(depth) / MillimetresPerMetre);

        /// <summary>The pollution a client receives for <paramref name="pollution"/>.</summary>
        public static float QuantizePollutionValue(float pollution) => QuantizePollution(pollution) / (float)PollutionSteps;

        private static int RawLength(int n, bool velocity) => (velocity ? 9 : 5) * n;

        private static int QuantizeDepthMillimetres(float depth)
        {
            if (float.IsNaN(depth)) return 0;
            double millimetres = Math.Round(depth * MillimetresPerMetre, MidpointRounding.AwayFromZero);
            if (millimetres > MaxDepthMillimetres) return MaxDepthMillimetres;
            if (millimetres < -MaxDepthMillimetres) return -MaxDepthMillimetres;
            return (int)millimetres;
        }

        private static byte QuantizePollution(float pollution)
        {
            if (!(pollution > 0f)) return 0;   // also NaN
            if (pollution >= 1f) return PollutionSteps;
            return (byte)Math.Round(pollution * (double)PollutionSteps, MidpointRounding.AwayFromZero);
        }

        private static ushort QuantizeVelocity(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            if (value > HalfFloat.MaxValue) value = HalfFloat.MaxValue;
            else if (value < -HalfFloat.MaxValue) value = -HalfFloat.MaxValue;
            return HalfFloat.FromSingle(value);
        }

        /// <summary>Checks every reconstructed value before anything is written to the caller's array.</summary>
        private static void Validate(byte[] raw, int n, bool velocity)
        {
            int depth = 0;
            ushort x = 0, y = 0;
            for (int i = 0; i < n; i++)
            {
                depth = unchecked(depth + UnZigZag32(ReadPlanes32(raw, n, i)));
                if (depth > MaxDepthMillimetres || depth < -MaxDepthMillimetres)
                {
                    throw new DecodeException(What + ": depth " + depth.ToString(CultureInfo.InvariantCulture) + " mm at texel " + i.ToString(CultureInfo.InvariantCulture) + " is out of range");
                }
                if (velocity)
                {
                    x = unchecked((ushort)(x + UnZigZag16((uint)(raw[5 * n + i] | (raw[6 * n + i] << 8)))));
                    y = unchecked((ushort)(y + UnZigZag16((uint)(raw[7 * n + i] | (raw[8 * n + i] << 8)))));
                    if (!HalfFloat.IsFinite(x) || !HalfFloat.IsFinite(y))
                    {
                        throw new DecodeException(What + ": non-finite velocity at texel " + i.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        private static uint ReadPlanes32(byte[] raw, int n, int i)
        {
            return raw[i] | ((uint)raw[n + i] << 8) | ((uint)raw[2 * n + i] << 16) | ((uint)raw[3 * n + i] << 24);
        }

        private static uint ZigZag16(short value) => (uint)((value << 1) ^ (value >> 15)) & 0xFFFFu;

        private static int UnZigZag16(uint zigzag) => (int)(zigzag >> 1) ^ -(int)(zigzag & 1);

        private static int UnZigZag32(uint zigzag) => unchecked((int)(zigzag >> 1) ^ -(int)(zigzag & 1));

        private static void CheckCount(int texelCount, int length)
        {
            if (texelCount < 0 || texelCount > MaxTexels)
            {
                throw new ArgumentOutOfRangeException(nameof(texelCount), texelCount, "a tile has 0 to " + MaxTexels + " texels");
            }
            if ((long)texelCount * ChannelsPerTexel > length)
            {
                throw new ArgumentOutOfRangeException(nameof(texelCount), texelCount, "rgba holds fewer than 4 × texelCount floats");
            }
        }
    }
}
