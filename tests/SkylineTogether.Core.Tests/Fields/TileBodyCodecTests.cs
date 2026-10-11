using System;
using SkylineTogether.Core.Buffers;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>
    /// The body framing shared by the tile codecs: a broken compressor never fails an encode; the tile is sent
    /// uncompressed (byte-identical to an encode without a compressor) and the failure is logged.
    /// </summary>
    public sealed class TileBodyCodecTests
    {
        private static readonly CompressorFault[] RecoverableFaults =
        {
            CompressorFault.ThrowInBound, CompressorFault.ZeroBound, CompressorFault.HugeBound,
            CompressorFault.ThrowInCompress, CompressorFault.NegativeLength, CompressorFault.LengthAboveBound
        };

        /// <summary>A smooth 64×64 terrain tile (large enough to be worth compressing).</summary>
        private static ushort[] Terrain()
        {
            var texels = new ushort[64 * 64];
            for (int i = 0; i < texels.Length; i++) texels[i] = (ushort)(8191 + 300 * Math.Sin(i * 0.01) + (i % 64));
            return texels;
        }

        private static float[] Water(int texels)
        {
            var random = new DeterministicRandom(55);
            var rgba = new float[texels * 4];
            for (int i = 0; i < texels; i++)
            {
                rgba[4 * i] = (float)(20 + random.NextDouble());
                rgba[4 * i + 1] = (float)random.NextDouble();
                rgba[4 * i + 2] = (float)-random.NextDouble();
                rgba[4 * i + 3] = (float)(random.NextDouble() * 0.1);
            }
            return rgba;
        }

        [Test]
        public void HeightTilesFallBackToAnUncompressedBodyAndLogTheFault()
        {
            ushort[] texels = Terrain();
            byte[] plain = FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, null));
            foreach (CompressorFault fault in RecoverableFaults)
            {
                var log = new ListLog();
                var compressor = new FaultyCompressor(fault);
                byte[] encoded = FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, compressor, log));
                Assert.SequenceEqual(plain, encoded, fault + ": the body is written uncompressed");
                Assert.Equal(1, log.Count(LogLevel.Warn), fault + ": one warning");
                Assert.True(log.Contains("[fields]") && log.Contains("height tile") && log.Contains("uncompressed"), fault + ": " + string.Join(" / ", log.Lines));
                var decoded = new ushort[texels.Length];
                HeightTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), decoded, decoded.Length, compressor);
                Assert.SequenceEqual(texels, decoded, fault + ": the fallback body decodes");
                // without a log the fallback is silent, but still never throws
                Assert.SequenceEqual(plain, FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, compressor)), fault + " without a log");
            }
        }

        [Test]
        public void WaterTilesFallBackToAnUncompressedBodyAndLogTheFault()
        {
            float[] rgba = Water(1024);
            byte[] plain = FieldTestSupport.Write(w => WaterTileCodec.Encode(rgba, 1024, WaterTileLevel.DepthPollutionVelocity, w, null));
            foreach (CompressorFault fault in RecoverableFaults)
            {
                var log = new ListLog();
                var compressor = new FaultyCompressor(fault);
                byte[] encoded = FieldTestSupport.Write(w => WaterTileCodec.Encode(rgba, 1024, WaterTileLevel.DepthPollutionVelocity, w, compressor, log));
                Assert.SequenceEqual(plain, encoded, fault + ": the body is written uncompressed");
                Assert.Equal(1, log.Count(LogLevel.Warn), fault + ": one warning");
                Assert.True(log.Contains("water tile"), fault + ": " + string.Join(" / ", log.Lines));
                var decoded = new float[1024 * 4];
                Assert.Equal(WaterTileLevel.DepthPollutionVelocity, WaterTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), decoded, 1024, null));
            }
        }

        [Test]
        public void OutOfMemoryIsNotSwallowed()
        {
            ushort[] texels = Terrain();
            var compressor = new FaultyCompressor(CompressorFault.OutOfMemoryInCompress);
            Assert.Throws<OutOfMemoryException>(() => HeightTileCodec.Encode(texels, texels.Length, new ByteWriter(), compressor, new ListLog()));
        }

        [Test]
        public void TheTwoLz4KindsShareOneFormat()
        {
            // native LZ4 and Core's managed LZ4 both write the LZ4 block format; Zstd is a different format
            ushort[] texels = Terrain();
            foreach (CompressionKind writerKind in new[] { CompressionKind.Lz4, CompressionKind.ManagedLz4 })
            {
                byte[] encoded = FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, new DeflateTestCompressor(writerKind)));
                foreach (CompressionKind readerKind in new[] { CompressionKind.Lz4, CompressionKind.ManagedLz4 })
                {
                    var decoded = new ushort[texels.Length];
                    HeightTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), decoded, decoded.Length, new DeflateTestCompressor(readerKind));
                    Assert.SequenceEqual(texels, decoded, writerKind + " body read with " + readerKind);
                }
                Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), new ushort[texels.Length], texels.Length,
                                                                            new DeflateTestCompressor(CompressionKind.Zstd)), writerKind + " body read with Zstd");
            }
        }

        [Test]
        public void ADecompressorThatMisreportsItsLengthIsADecodeError()
        {
            ushort[] texels = Terrain();
            byte[] encoded = FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, new DeflateTestCompressor()));
            var target = new ushort[texels.Length];
            Assert.Throws<DecodeException>(() => HeightTileCodec.Decode(new ByteReader(encoded, 0, encoded.Length), target, target.Length, new ShortDecompressor()));
            Assert.SequenceEqual(new ushort[texels.Length], target, "nothing was written");
        }

        /// <summary>Decompresses correctly but claims one byte less than it was asked for (a contract violation).</summary>
        private sealed class ShortDecompressor : ICompressor
        {
            private readonly DeflateTestCompressor _inner = new DeflateTestCompressor();

            public CompressionKind Kind => _inner.Kind;

            public int MaxCompressedSize(int length) => _inner.MaxCompressedSize(length);

            public int Compress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset) => _inner.Compress(src, srcOffset, srcCount, dst, dstOffset);

            public int Decompress(byte[] src, int srcOffset, int srcCount, byte[] dst, int dstOffset, int expectedLength)
            {
                return _inner.Decompress(src, srcOffset, srcCount, dst, dstOffset, expectedLength) - 1;
            }
        }

        [Test]
        public void AWorkingCompressorLogsNothing()
        {
            ushort[] texels = Terrain();
            var log = new ListLog();
            var deflate = new DeflateTestCompressor();
            byte[] encoded = FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, deflate, log));
            Assert.Equal(0, log.Lines.Count);
            Assert.Equal(1, deflate.CompressCalls);
            Assert.SequenceEqual(FieldTestSupport.Write(w => HeightTileCodec.Encode(texels, texels.Length, w, deflate)), encoded, "the log does not change the bytes");
        }
    }
}
