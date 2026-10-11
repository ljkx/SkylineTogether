using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>WaterTileCodec: quantisation error bounds (depth 1 mm, pollution 1/255), levels, special values, malformed input.</summary>
    public sealed class WaterTileCodecTests
    {
        private const int TileTexels = 256 * 256;

        /// <summary>A plausible water tile: a sea with a shore (dry land with zero depth), a polluted river and flow.</summary>
        private static float[] SeaTile(int size, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var rgba = new float[size * size * 4];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int t = (y * size + x) * 4;
                    double ground = 400 + 0.9 * x + 30 * Math.Sin(y * 0.05);
                    double depth = Math.Max(0, 511.7 - ground);
                    rgba[t] = (float)depth;
                    rgba[t + 1] = depth > 0 ? (float)(Math.Sin(x * 0.1) * 3 + random.NextDouble() * 0.01) : 0f;
                    rgba[t + 2] = depth > 0 ? (float)(Math.Cos(y * 0.1) * 2) : 0f;
                    rgba[t + 3] = depth > 0 && Math.Abs(y - size / 2) < 6 ? (float)random.NextDouble() : 0f;
                }
            }
            return rgba;
        }

        private static byte[] Encode(float[] rgba, int count, WaterTileLevel level, ICompressor compressor)
        {
            return FieldTestSupport.Write(w => WaterTileCodec.Encode(rgba, count, level, w, compressor));
        }

        private static float[] Decode(byte[] encoded, int count, ICompressor compressor, out WaterTileLevel level)
        {
            var rgba = new float[count * 4];
            var reader = new ByteReader(encoded, 0, encoded.Length);
            level = WaterTileCodec.Decode(reader, rgba, count, compressor);
            Assert.True(reader.IsAtEnd, "the decoder consumes the whole tile");
            return rgba;
        }

        [Test]
        public void QuantisationStaysWithinTheErrorBounds()
        {
            var random = new DeterministicRandom(1010);
            const int count = 20000;
            var rgba = new float[count * 4];
            for (int i = 0; i < count; i++)
            {
                rgba[4 * i] = (float)(random.NextDouble() * 3010 - 10);   // includes negative and very deep values
                rgba[4 * i + 1] = (float)(random.NextDouble() * 24 - 12);
                rgba[4 * i + 2] = (float)(random.NextDouble() * 0.02 - 0.01);
                rgba[4 * i + 3] = (float)random.NextDouble();
            }
            var deflate = new DeflateTestCompressor();
            float[] decoded = Decode(Encode(rgba, count, WaterTileLevel.DepthPollutionVelocity, deflate), count, deflate, out WaterTileLevel level);
            Assert.Equal(WaterTileLevel.DepthPollutionVelocity, level);
            for (int i = 0; i < count; i++)
            {
                float depth = rgba[4 * i], decodedDepth = decoded[4 * i];
                Assert.True(Math.Abs(decodedDepth - depth) <= 0.001, "depth " + depth + " → " + decodedDepth + " (texel " + i + ")");
                Assert.True(Math.Abs(decodedDepth - depth) <= 0.0005 + Ulp(depth), "depth within half a millimetre plus float rounding (texel " + i + ")");
                Assert.True(Math.Abs(decoded[4 * i + 3] - rgba[4 * i + 3]) <= 1.0 / 510 + 1e-6, "pollution (texel " + i + ")");
                for (int c = 1; c <= 2; c++)
                {
                    float v = rgba[4 * i + c], d = decoded[4 * i + c];
                    double tolerance = Math.Max(Math.Abs(v) * Math.Pow(2, -11), Math.Pow(2, -25));
                    Assert.True(Math.Abs(d - v) <= tolerance, "velocity " + v + " → " + d + " (texel " + i + ")");
                }
            }
        }

        [Test]
        public void DeepSeaKeepsMillimetrePrecision()
        {
            // the design's u16 millimetres would saturate at 65.535 m; the default sea level is 511.7 m (R07 §6.1)
            float[] rgba = { 511.7f, 0f, 0f, 0f, 65.536f, 0f, 0f, 0.5f, 2000.123f, 0f, 0f, 1f, 0.0004f, 0f, 0f, 0.002f };
            float[] decoded = Decode(Encode(rgba, 4, WaterTileLevel.DepthPollution, null), 4, null, out _);
            Assert.Near(511.7, decoded[0], 0.0006);
            Assert.Near(65.536, decoded[4], 0.0006);
            Assert.Near(2000.123, decoded[8], 0.0007);
            Assert.Equal(0f, decoded[12], "0.4 mm rounds to 0");
            Assert.Equal(WaterTileCodec.QuantizeDepth(511.7f), decoded[0], "QuantizeDepth predicts the received value");
            Assert.Equal(WaterTileCodec.QuantizePollutionValue(0.002f), decoded[15]);
        }

        [Test]
        public void DepthPollutionLevelWritesZeroVelocity()
        {
            float[] sea = SeaTile(64, 3);
            var deflate = new DeflateTestCompressor();
            float[] decoded = Decode(Encode(sea, 64 * 64, WaterTileLevel.DepthPollution, deflate), 64 * 64, deflate, out WaterTileLevel level);
            Assert.Equal(WaterTileLevel.DepthPollution, level);
            for (int i = 0; i < 64 * 64; i++)
            {
                Assert.Equal(0f, decoded[4 * i + 1], "velocity x of texel " + i);
                Assert.Equal(0f, decoded[4 * i + 2], "velocity y of texel " + i);
                Assert.True(Math.Abs(decoded[4 * i] - sea[4 * i]) <= 0.001, "depth of texel " + i);
            }
        }

        [Test]
        public void SpecialValuesAreSanitised()
        {
            float[] rgba =
            {
                float.NaN, float.NaN, float.PositiveInfinity, float.NaN,
                float.PositiveInfinity, 1e9f, -1e9f, 7f,
                float.NegativeInfinity, -0f, 0f, -3f,
                -0.25f, 65519f, -70000f, 1.0000001f,
            };
            float[] decoded = Decode(Encode(rgba, 4, WaterTileLevel.DepthPollutionVelocity, null), 4, null, out _);
            Assert.Equal(0f, decoded[0], "NaN depth → 0");
            Assert.Equal(0f, decoded[1], "NaN velocity → 0");
            Assert.Equal(0f, decoded[2], "infinite velocity → 0");
            Assert.Equal(0f, decoded[3], "NaN pollution → 0");
            Assert.Equal((float)(WaterTileCodec.MaxDepthMillimetres / 1000.0), decoded[4], "+∞ depth is clamped");
            Assert.Equal(65504f, decoded[5], "huge velocity is clamped to the half range");
            Assert.Equal(-65504f, decoded[6]);
            Assert.Equal(1f, decoded[7], "pollution above 1 is clamped");
            Assert.Equal(-(float)(WaterTileCodec.MaxDepthMillimetres / 1000.0), decoded[8], "-∞ depth is clamped");
            Assert.Equal(0f, decoded[11], "negative pollution → 0");
            Assert.Near(-0.25, decoded[12], 1e-6);
            Assert.Equal(65504f, decoded[13], "65519 rounds to the largest half");
            Assert.Equal(-65504f, decoded[14]);
            Assert.Equal(1f, decoded[15]);
            foreach (float value in decoded) Assert.False(float.IsNaN(value) || float.IsInfinity(value), "every decoded value is finite");
        }

        [Test]
        public void QuantisedTilesAreFixedPoints()
        {
            float[] sea = SeaTile(128, 4);
            var deflate = new DeflateTestCompressor();
            foreach (WaterTileLevel level in new[] { WaterTileLevel.DepthPollution, WaterTileLevel.DepthPollutionVelocity })
            {
                float[] once = Decode(Encode(sea, 128 * 128, level, deflate), 128 * 128, deflate, out _);
                byte[] first = Encode(once, 128 * 128, level, deflate);
                float[] twice = Decode(first, 128 * 128, deflate, out _);
                Assert.SequenceEqual(once, twice, "re-encoding received values changes nothing (" + level + ")");
                Assert.SequenceEqual(first, Encode(twice, 128 * 128, level, deflate), "deterministic bytes (" + level + ")");
            }
        }

        [Test]
        public void FullTilesCompressWell()
        {
            float[] sea = SeaTile(256, 5);
            var deflate = new DeflateTestCompressor();
            byte[] low = Encode(sea, TileTexels, WaterTileLevel.DepthPollution, deflate);
            byte[] high = Encode(sea, TileTexels, WaterTileLevel.DepthPollutionVelocity, deflate);
            Assert.True(low.Length < TileTexels * 3 / 4, "depth and pollution of a 256² tile cost " + low.Length + " bytes");
            Assert.True(high.Length > low.Length, "velocity adds data");
            var dry = new float[TileTexels * 4];
            byte[] dryTile = Encode(dry, TileTexels, WaterTileLevel.DepthPollutionVelocity, null);
            Assert.True(dryTile.Length < 40, "a dry tile costs " + dryTile.Length + " bytes even without a compressor");
            Assert.SequenceEqual(dry, Decode(dryTile, TileTexels, null, out _));
        }

        [Test]
        public void DecodeRejectsMismatchesAndUnknownFormats()
        {
            float[] sea = SeaTile(16, 6);
            byte[] encoded = Encode(sea, 256, WaterTileLevel.DepthPollution, null);
            var rgba = new float[256 * 4];
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), new float[255 * 4], 255, null), "texel count");
            byte[] badVersion = (byte[])encoded.Clone();
            badVersion[0] = 0;
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(badVersion, 0, badVersion.Length), rgba, 256, null), "version");
            byte[] badLevel = (byte[])encoded.Clone();
            badLevel[1] = 2;
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(badLevel, 0, badLevel.Length), rgba, 256, null), "level");
            var deflate = new DeflateTestCompressor();
            byte[] compressed = Encode(SeaTile(64, 7), 4096, WaterTileLevel.DepthPollutionVelocity, deflate);
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(compressed, 0, compressed.Length), new float[4096 * 4], 4096, null), "no compressor");
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(compressed, 0, compressed.Length), new float[4096 * 4], 4096, new ThrowingDecompressor()), "failing decompressor");
        }

        [Test]
        public void OutOfRangeDepthAndNonFiniteVelocityAreRejected()
        {
            // a well-framed body whose planes decode to a depth beyond the limit
            byte[] hugeDepth = CraftTile(1, WaterTileLevel.DepthPollution, raw =>
            {
                uint zigzag = (uint)(WaterTileCodec.MaxDepthMillimetres + 1) << 1;
                raw[0] = (byte)zigzag;
                raw[1] = (byte)(zigzag >> 8);
                raw[2] = (byte)(zigzag >> 16);
                raw[3] = (byte)(zigzag >> 24);
            });
            var rgba = new float[4];
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(hugeDepth, 0, hugeDepth.Length), rgba, 1, null), "depth beyond the limit");
            // a velocity whose half bits are an infinity (0x7C00, zig-zag 0xF800)
            byte[] infiniteVelocity = CraftTile(1, WaterTileLevel.DepthPollutionVelocity, raw =>
            {
                raw[5] = 0x00;
                raw[6] = 0xF8;
            });
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(infiniteVelocity, 0, infiniteVelocity.Length), rgba, 1, null), "infinite velocity");
            Assert.Equal(0f, rgba[0], "nothing was written");
            byte[] fine = CraftTile(1, WaterTileLevel.DepthPollutionVelocity, raw => raw[6] = 0x78);   // half 0x3C00 = 1.0
            WaterTileCodec.Decode(new ByteReader(fine, 0, fine.Length), rgba, 1, null);
            Assert.Equal(1f, rgba[1], "the crafted planes decode as expected");
        }

        [Test]
        public void TruncatedAndCorruptTilesOnlyThrowDecodeException()
        {
            float[] sea = SeaTile(32, 8);
            var deflate = new DeflateTestCompressor();
            foreach (ICompressor compressor in new ICompressor[] { null, deflate })
            {
                foreach (WaterTileLevel level in new[] { WaterTileLevel.DepthPollution, WaterTileLevel.DepthPollutionVelocity })
                {
                    byte[] encoded = Encode(sea, 1024, level, compressor);
                    var rgba = new float[1024 * 4];
                    FieldTestSupport.AssertEveryTruncationFails(encoded, r => WaterTileCodec.Decode(r, rgba, 1024, compressor), "water tile");
                    FieldTestSupport.AssertCorruptionOnlyThrowsDecodeException(encoded, r => WaterTileCodec.Decode(r, rgba, 1024, compressor), 77, 600, "water tile");
                }
            }
        }

        [Test]
        public void FailedDecodeLeavesTheTileUntouched()
        {
            byte[] encoded = Encode(SeaTile(16, 9), 256, WaterTileLevel.DepthPollutionVelocity, null);
            Array.Resize(ref encoded, encoded.Length - 1);
            var rgba = new float[256 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = 42f;
            Assert.Throws<DecodeException>(() => WaterTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), rgba, 256, null));
            foreach (float value in rgba) Assert.Equal(42f, value);
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            var writer = new ByteWriter();
            Assert.Throws<ArgumentNullException>(() => WaterTileCodec.Encode(null, 0, WaterTileLevel.DepthPollution, writer, null));
            Assert.Throws<ArgumentNullException>(() => WaterTileCodec.Encode(new float[4], 1, WaterTileLevel.DepthPollution, null, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => WaterTileCodec.Encode(new float[7], 2, WaterTileLevel.DepthPollution, writer, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => WaterTileCodec.Encode(new float[4], -1, WaterTileLevel.DepthPollution, writer, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => WaterTileCodec.Encode(new float[4], 1, (WaterTileLevel)7, writer, null));
            Assert.Throws<ArgumentNullException>(() => WaterTileCodec.Decode(null, new float[4], 1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => WaterTileCodec.Decode(new ByteReader(), new float[4], 2, null));
            Assert.Equal(0, writer.Length, "nothing was written by the failed calls");
        }

        /// <summary>Builds a tile from raw planes (zero-initialised, adjusted by <paramref name="fill"/>) with the real body framing.</summary>
        private static byte[] CraftTile(int count, WaterTileLevel level, Action<byte[]> fill)
        {
            int rawLength = (level == WaterTileLevel.DepthPollution ? 5 : 9) * count;
            var raw = new byte[rawLength];
            fill(raw);
            return FieldTestSupport.Write(w =>
            {
                w.WriteByte(WaterTileCodec.FormatVersion);
                w.WriteByte((byte)level);
                w.WriteVarUInt32((uint)count);
                TileBodyCodec.Write(raw, rawLength, null, w, null, "water tile");
            });
        }

        private static double Ulp(float value)
        {
            float magnitude = Math.Abs(value);
            return Math.Max(MathF.BitIncrement(magnitude) - magnitude, float.Epsilon);
        }
    }
}
