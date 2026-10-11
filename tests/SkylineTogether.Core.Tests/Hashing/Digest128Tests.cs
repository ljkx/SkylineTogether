using System;
using System.IO;
using System.Text;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Hashing
{
    /// <summary>Digest128 (XXH3-128, seed 0): stability, streaming equivalence and the Stream overload.</summary>
    public sealed class Digest128Tests
    {
        private static Digest128 Of(string ascii)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(ascii);
            return Digest128.Compute(bytes, 0, bytes.Length);
        }

        [Test]
        public void EmptyInputMatchesTheReferenceValue()
        {
            // XXH3_128bits("") of the reference library: 99aa06d3014798d8 6001c324468d497f
            Digest128 empty = Digest128.Compute(new byte[0], 0, 0);
            Assert.Equal(0x99AA06D3014798D8UL, empty.Hi);
            Assert.Equal(0x6001C324468D497FUL, empty.Lo);
            Assert.Equal("99aa06d3014798d86001c324468d497f", empty.ToString());
        }

        [Test]
        public void DigestsAreStable()
        {
            // pinned outputs (verified against System.IO.Hashing.XxHash128 when it is available): the snapshot
            // manifest and warm-join block lists depend on these values never changing
            Assert.Equal(DigestStabilityValues.Abc, Of("abc"));
            Assert.Equal(DigestStabilityValues.Fox, Of("The quick brown fox jumps over the lazy dog"));
            Assert.Equal(DigestStabilityValues.Pattern1000, Digest128.Compute(DigestStabilityValues.Pattern(1000), 0, 1000));
            Assert.Equal(DigestStabilityValues.Pattern5000, Digest128.Compute(DigestStabilityValues.Pattern(5000), 0, 5000));
        }

        [Test]
        public void EveryLengthClassIsCoveredAndDistinct()
        {
            // lengths around every branch boundary of XXH3-128: 0, 1-3, 4-8, 9-16, 17-128, 129-240, long, block edges
            byte[] data = DigestStabilityValues.Pattern(4200);
            int[] lengths = { 0, 1, 2, 3, 4, 5, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 96, 97, 127, 128, 129, 159, 160, 161, 192, 239, 240,
                              241, 255, 256, 257, 511, 512, 1023, 1024, 1025, 2047, 2048, 2049, 4096, 4200 };
            var seen = new System.Collections.Generic.HashSet<Digest128>();
            foreach (int length in lengths)
            {
                Digest128 digest = Digest128.Compute(data, 0, length);
                Assert.True(seen.Add(digest), "distinct digest for length " + length);
            }
        }

        [Test]
        public void StreamingMatchesOneShotAcrossSplits()
        {
            var random = new DeterministicRandom(128);
            var data = new byte[3000];
            random.NextBytes(data, 0, data.Length);
            int[] lengths = { 0, 1, 16, 17, 128, 129, 240, 241, 255, 256, 257, 300, 1024, 1025, 2048, 2049, 3000 };
            foreach (int length in lengths)
            {
                Digest128 expected = Digest128.Compute(data, 0, length);
                for (int split = 0; split <= length; split += Math.Max(1, length / 13))
                {
                    var stream = new XxHash128Stream();
                    stream.Append(data, 0, split);
                    stream.Append(data, split, length - split);
                    Assert.Equal(expected, stream.Finish(), "length " + length + " split " + split);
                }
            }
        }

        [Test]
        public void StreamingInRandomPiecesWithIntermediateFinishes()
        {
            var random = new DeterministicRandom(129);
            var data = new byte[20000];
            random.NextBytes(data, 0, data.Length);
            var stream = new XxHash128Stream();
            int position = 0;
            while (position < data.Length)
            {
                int piece = Math.Min(random.Next(700), data.Length - position);
                stream.Append(data, position, piece);
                position += piece;
                if (random.Next(4) == 0)
                {
                    Assert.Equal(Digest128.Compute(data, 0, position), stream.Finish(), "after " + position + " bytes");
                }
            }
            Assert.Equal(Digest128.Compute(data, 0, data.Length), stream.Finish());
            Assert.Equal((ulong)data.Length, stream.Length);
            stream.Reset();
            Assert.Equal(Digest128.Compute(new byte[0], 0, 0), stream.Finish());
        }

        [Test]
        public void StreamOverloadReadsToTheEndInChunks()
        {
            var random = new DeterministicRandom(130);
            var data = new byte[300000];   // several 64 KiB chunks
            random.NextBytes(data, 0, data.Length);
            using (var stream = new MemoryStream(data))
            {
                Assert.Equal(Digest128.Compute(data, 0, data.Length), Digest128.Compute(stream));
            }
            using (var stream = new MemoryStream(data))
            {
                stream.Position = 1000;
                Assert.Equal(Digest128.Compute(data, 1000, data.Length - 1000), Digest128.Compute(stream), "from the current position");
            }
            Assert.Throws<ArgumentNullException>(() => Digest128.Compute((Stream)null));
        }

        [Test]
        public void EqualityAndArguments()
        {
            var a = new Digest128(1, 2);
            Assert.True(a == new Digest128(1, 2));
            Assert.True(a != new Digest128(2, 1));
            Assert.Equal(a.GetHashCode(), new Digest128(1, 2).GetHashCode());
            Assert.Equal("00000000000000020000000000000001", a.ToString());
            Assert.Throws<ArgumentNullException>(() => Digest128.Compute(null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Digest128.Compute(new byte[3], 2, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => new XxHash128Stream().Append(new byte[3], 0, 4));
        }
    }

    /// <summary>Pinned digest values and the deterministic input pattern they are computed over.</summary>
    internal static class DigestStabilityValues
    {
        // XXH3_128bits("abc") of the reference library is 06b05ab6733a6185 78af5f94892f3950
        public static readonly Digest128 Abc = new Digest128(0x78AF5F94892F3950UL, 0x06B05AB6733A6185UL);
        public static readonly Digest128 Fox = new Digest128(0x24A1CC2E3A8A7651UL, 0xDDD650205CA3E7FAUL);
        public static readonly Digest128 Pattern1000 = new Digest128(0x0DC54DA2182285F1UL, 0x5B9A536290E85D63UL);
        public static readonly Digest128 Pattern5000 = new Digest128(0xC0DD97972A4D2DE3UL, 0x47AA050A1D2C0B8CUL);

        /// <summary>Bytes i * 31 + (i >> 8) (mod 256): a fixed pattern without long runs.</summary>
        public static byte[] Pattern(int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = unchecked((byte)(i * 31 + (i >> 8)));
            return data;
        }
    }
}
