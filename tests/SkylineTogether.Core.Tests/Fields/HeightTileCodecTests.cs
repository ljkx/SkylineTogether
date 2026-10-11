using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>HeightTileCodec: round trips, compression choice, malformed input and the stable tile hash.</summary>
    public sealed class HeightTileCodecTests
    {
        private const int TileTexels = 64 * 64;

        private static ushort[] Flat(int count, ushort value)
        {
            var texels = new ushort[count];
            for (int i = 0; i < count; i++) texels[i] = value;
            return texels;
        }

        /// <summary>A smooth hill over a 64×64 tile, as a terrain tile looks after a few strokes.</summary>
        private static ushort[] Hill(int size)
        {
            var texels = new ushort[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double d = Math.Sqrt((x - size / 2.0) * (x - size / 2.0) + (y - size / 2.0) * (y - size / 2.0));
                    texels[y * size + x] = (ushort)(8191 + 900 * Math.Exp(-d * d / 300.0) + 40 * Math.Sin(x * 0.2));
                }
            }
            return texels;
        }

        private static ushort[] RandomTexels(int count, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var texels = new ushort[count];
            for (int i = 0; i < count; i++) texels[i] = (ushort)random.Next(65536);
            return texels;
        }

        private static byte[] Encode(ushort[] texels, ICompressor compressor)
        {
            return FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, compressor));
        }

        private static ushort[] Decode(byte[] encoded, int count, ICompressor compressor)
        {
            var texels = new ushort[count];
            var reader = new ByteReader(encoded, 0, encoded.Length);
            HeightTileCodec.Decode(reader, texels, count, compressor);
            Assert.True(reader.IsAtEnd, "the decoder consumes the whole tile");
            return texels;
        }

        [Test]
        public void RoundTripsEveryPatternWithAndWithoutCompression()
        {
            var alternating = new ushort[TileTexels];
            for (int i = 0; i < alternating.Length; i++) alternating[i] = (ushort)(i % 2 == 0 ? 0 : 65535);
            var ramp = new ushort[TileTexels];
            for (int i = 0; i < ramp.Length; i++) ramp[i] = (ushort)(i * 16);
            var spike = Flat(TileTexels, 8191);
            spike[2000] = 65535;
            ushort[][] patterns = { Flat(TileTexels, 8191), Flat(TileTexels, 0), Flat(TileTexels, 65535), Hill(64), ramp, alternating, spike, RandomTexels(TileTexels, 11) };
            ICompressor[] compressors = { null, new DeflateTestCompressor(), new ExpandingTestCompressor() };
            for (int p = 0; p < patterns.Length; p++)
            {
                foreach (ICompressor compressor in compressors)
                {
                    byte[] encoded = Encode(patterns[p], compressor);
                    Assert.SequenceEqual(patterns[p], Decode(encoded, patterns[p].Length, compressor), "pattern " + p + " with " + (compressor?.GetType().Name ?? "no compressor"));
                }
            }
        }

        [Test]
        public void RoundTripsOddTexelCounts()
        {
            var compressor = new DeflateTestCompressor();
            foreach (int count in new[] { 0, 1, 2, 7, 63, 100, 1000, 4095, 65536 })
            {
                ushort[] texels = RandomTexels(count, (ulong)count + 5);
                Assert.SequenceEqual(texels, Decode(Encode(texels, compressor), count, compressor), "count " + count);
                Assert.SequenceEqual(texels, Decode(Encode(texels, null), count, null), "count " + count + " uncompressed");
            }
        }

        [Test]
        public void SmoothAndFlatTilesAreSmall()
        {
            byte[] flat = Encode(Flat(TileTexels, 8191), null);
            Assert.True(flat.Length < 24, "a flat tile costs " + flat.Length + " bytes without a compressor");
            byte[] hill = Encode(Hill(64), new DeflateTestCompressor());
            Assert.True(hill.Length < 8192 / 3, "a smooth tile costs " + hill.Length + " of 8192 raw bytes");
        }

        [Test]
        public void CompressesOnlyWhenTheResultIsSmaller()
        {
            var deflate = new DeflateTestCompressor();
            byte[] hill = Encode(Hill(64), deflate);
            Assert.Equal(1, deflate.CompressCalls);
            Assert.Equal((byte)TileBodyCodec.FlagCompressed, BodyFlags(hill), "the hill compresses");
            Assert.Equal((byte)CompressionKind.Zstd, hill[BodyFlagsOffset(hill) + 1], "the compression kind follows the flags");

            var expanding = new ExpandingTestCompressor();
            byte[] raw = Encode(Hill(64), expanding);
            Assert.Equal(1, expanding.CompressCalls);
            Assert.Equal((byte)0, BodyFlags(raw), "a larger compressed form is not used");
            Assert.SequenceEqual(Hill(64), Decode(raw, TileTexels, expanding));

            var tiny = new DeflateTestCompressor();
            Encode(Flat(TileTexels, 1), tiny);
            Assert.Equal(0, tiny.CompressCalls, "bodies below the threshold are not compressed at all");
            Assert.Equal((byte)0, BodyFlags(Encode(Hill(64), new NoneCompressor())), "CompressionKind.None means uncompressed");
        }

        [Test]
        public void EncodingIsDeterministicAndIndependentOfPooledBuffers()
        {
            var compressor = new DeflateTestCompressor();
            ushort[] hill = Hill(64);
            byte[] first = Encode(hill, compressor);
            Encode(RandomTexels(65536, 99), compressor);   // dirties pooled buffers with other data
            Encode(Flat(10, 3), compressor);
            Assert.SequenceEqual(first, Encode(hill, compressor));
            byte[] withPrefix = FieldTestSupport.Write(w =>
            {
                w.WriteUInt32(0xDEADBEEF);
                HeightTileCodec.Encode(hill, hill.Length, w, compressor);
            });
            var reader = new ByteReader(withPrefix, 4, withPrefix.Length - 4);
            var texels = new ushort[hill.Length];
            HeightTileCodec.Decode(reader, texels, texels.Length, compressor);
            Assert.SequenceEqual(hill, texels, "appends to existing writer content and decodes from an offset");
        }

        [Test]
        public void DecodeRejectsMismatchesAndUnknownFormats()
        {
            ushort[] hill = Hill(64);
            var deflate = new DeflateTestCompressor();
            byte[] compressed = Encode(hill, deflate);
            byte[] raw = Encode(hill, null);
            var texels = new ushort[TileTexels];
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(raw, 0, raw.Length), new ushort[4095], 4095, null), "texel count");
            byte[] badVersion = (byte[])raw.Clone();
            badVersion[0] = 2;
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(badVersion, 0, badVersion.Length), texels, TileTexels, null), "version");
            byte[] badFlags = (byte[])raw.Clone();
            badFlags[BodyFlagsOffset(raw)] = 0x80;
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(badFlags, 0, badFlags.Length), texels, TileTexels, null), "flags");
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(compressed, 0, compressed.Length), texels, TileTexels, null), "compressed without a compressor");
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(compressed, 0, compressed.Length), texels, TileTexels, new DeflateTestCompressor(CompressionKind.ManagedLz4)), "another compressor kind");
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(compressed, 0, compressed.Length), texels, TileTexels, new ThrowingDecompressor()), "a failing native decompressor");
            byte[] badKind = (byte[])compressed.Clone();
            badKind[BodyFlagsOffset(compressed) + 1] = 9;
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(badKind, 0, badKind.Length), texels, TileTexels, deflate), "unknown kind");
        }

        [Test]
        public void TruncatedAndCorruptTilesOnlyThrowDecodeException()
        {
            ushort[] hill = Hill(64);
            var deflate = new DeflateTestCompressor();
            foreach (ICompressor compressor in new ICompressor[] { null, deflate })
            {
                byte[] encoded = Encode(hill, compressor);
                var texels = new ushort[TileTexels];
                FieldTestSupport.AssertEveryTruncationFails(encoded, r => HeightTileCodec.Decode(r, texels, TileTexels, compressor), "height tile");
                FieldTestSupport.AssertCorruptionOnlyThrowsDecodeException(encoded, r => HeightTileCodec.Decode(r, texels, TileTexels, compressor), 123, 1500, "height tile");
            }
        }

        [Test]
        public void FailedDecodeLeavesTheTexelsUntouched()
        {
            byte[] encoded = Encode(Hill(64), null);
            byte[] corrupt = (byte[])encoded.Clone();
            corrupt[corrupt.Length - 1] ^= 0xFF;
            Array.Resize(ref corrupt, corrupt.Length - 3);
            ushort[] texels = Flat(TileTexels, 4242);
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(corrupt, 0, corrupt.Length), texels, TileTexels, null));
            Assert.SequenceEqual(Flat(TileTexels, 4242), texels);
        }

        [Test]
        public void HashIsXxHash64OfLittleEndianTexels()
        {
            ushort[] hill = Hill(64);
            var bytes = new byte[hill.Length * 2];
            for (int i = 0; i < hill.Length; i++)
            {
                bytes[2 * i] = (byte)hill[i];
                bytes[2 * i + 1] = (byte)(hill[i] >> 8);
            }
            Assert.Equal(XxHash64.Hash(bytes, 0, bytes.Length), HeightTileCodec.Hash(hill, hill.Length));
            Assert.Equal(XxHash64.Hash(bytes, 0, 200), HeightTileCodec.Hash(hill, 100), "only count texels are hashed");
            Assert.Equal(XxHash64.Hash(new byte[0], 0, 0), HeightTileCodec.Hash(hill, 0));
        }

        [Test]
        public void HashIsStable()
        {
            // pinned: host and client compare these hashes in the tile audit, so the function must never change
            ushort[] flat = Flat(TileTexels, 8191);
            ushort[] pattern = PinPattern();
            Assert.Equal(PinnedFlatHash, HeightTileCodec.Hash(flat, TileTexels));
            Assert.Equal(PinnedPatternHash, HeightTileCodec.Hash(pattern, TileTexels));
#if HAVE_SYSTEM_IO_HASHING
            // the pins are xxHash64 of the little-endian texels by Microsoft's implementation, too
            Assert.Equal(PinnedFlatHash, ReferenceHash(flat));
            Assert.Equal(PinnedPatternHash, ReferenceHash(pattern));
#endif
        }

        private static ushort[] PinPattern()
        {
            var pattern = new ushort[TileTexels];
            for (int i = 0; i < pattern.Length; i++) pattern[i] = unchecked((ushort)(i * 2654435761u >> 7));
            return pattern;
        }

#if HAVE_SYSTEM_IO_HASHING
        private static ulong ReferenceHash(ushort[] texels)
        {
            var bytes = new byte[texels.Length * 2];
            for (int i = 0; i < texels.Length; i++)
            {
                bytes[2 * i] = (byte)texels[i];
                bytes[2 * i + 1] = (byte)(texels[i] >> 8);
            }
            byte[] bigEndian = System.IO.Hashing.XxHash64.Hash(bytes);
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | bigEndian[i];
            return value;
        }
#endif

        [Test]
        public void HashChangesWithEveryTexel()
        {
            ushort[] hill = Hill(64);
            ulong reference = HeightTileCodec.Hash(hill, hill.Length);
            foreach (int index in new[] { 0, 1, 63, 64, 2047, 4094, 4095 })
            {
                ushort[] changed = (ushort[])hill.Clone();
                changed[index] ^= 1;   // one LSB, the GPU-caveat size of a difference (R07 §5.2)
                Assert.NotEqual(reference, HeightTileCodec.Hash(changed, changed.Length), "texel " + index);
            }
        }

        [Test]
        public void ArgumentsAreValidated()
        {
            var writer = new ByteWriter();
            Assert.Throws<ArgumentNullException>(() => HeightTileCodec.Encode(null, 0, writer, null));
            Assert.Throws<ArgumentNullException>(() => HeightTileCodec.Encode(new ushort[1], 1, null, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeightTileCodec.Encode(new ushort[1], 2, writer, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeightTileCodec.Encode(new ushort[1], -1, writer, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeightTileCodec.Encode(new ushort[HeightTileCodec.MaxTexels + 1], HeightTileCodec.MaxTexels + 1, writer, null));
            Assert.Throws<ArgumentNullException>(() => HeightTileCodec.Decode(null, new ushort[1], 1, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeightTileCodec.Decode(new ByteReader(), new ushort[1], 2, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeightTileCodec.Hash(new ushort[1], 2));
            Assert.Throws<ArgumentNullException>(() => HeightTileCodec.Hash(null, 0));
        }

        internal const ulong PinnedFlatHash = 0xD0E34D2B2E4FC016UL;
        internal const ulong PinnedPatternHash = 0xA822BA76E3D272BEUL;

        /// <summary>Offset of the body flags byte: version (1 byte) + varuint texel count.</summary>
        private static int BodyFlagsOffset(byte[] encoded)
        {
            var reader = new ByteReader(encoded, 0, encoded.Length);
            reader.ReadByte();
            reader.ReadVarUInt32();
            return reader.Position;
        }

        private static byte BodyFlags(byte[] encoded) => encoded[BodyFlagsOffset(encoded)];

        private sealed class NoneCompressor : ICompressor
        {
            public CompressionKind Kind => CompressionKind.None;

            public int MaxCompressedSize(int length) => length;

            public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset) => throw new InvalidOperationException("not used");

            public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength) => throw new InvalidOperationException("not used");
        }
    }
}
