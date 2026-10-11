using SkylineTogether.Core.Codec;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Codec
{
    /// <summary>The strided byte filter of the canonical layout: a length-preserving, invertible transposition.</summary>
    public sealed class ByteShuffleTests
    {
        [Test]
        public void UnshuffleInvertsShuffleForEveryLengthAndStride()
        {
            var random = new DeterministicRandom(1234);
            for (int stride = -1; stride <= 17; stride++)
            {
                for (int count = 0; count <= 70; count++)
                {
                    var source = new byte[count + 6];
                    random.NextBytes(source, 0, source.Length);
                    var shuffled = new byte[count + 4];
                    var restored = new byte[count + 2];
                    ByteShuffle.Shuffle(source, 3, shuffled, 2, count, stride);
                    ByteShuffle.Unshuffle(shuffled, 2, restored, 1, count, stride);
                    for (int i = 0; i < count; i++)
                    {
                        if (restored[1 + i] != source[3 + i]) Assert.Fail("stride " + stride + ", count " + count + ": byte " + i + " differs");
                    }
                    Assert.Equal((byte)0, shuffled[0], "nothing written before the destination range");
                    Assert.Equal((byte)0, shuffled[shuffled.Length - 1], "nothing written after the destination range");
                }
            }
        }

        [Test]
        public void ShuffleGroupsTheBytesOfEachLane()
        {
            // four little-endian ints 1, 2, 3, 4 (stride 4): the low bytes first, then three lanes of zeros
            byte[] ints = { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0 };
            var shuffled = new byte[16];
            ByteShuffle.Shuffle(ints, 0, shuffled, 0, 16, 4);
            Assert.SequenceEqual(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, shuffled);
        }

        [Test]
        public void IdentityCases()
        {
            byte[] data = { 9, 8, 7, 6, 5 };
            foreach (int stride in new[] { int.MinValue, -3, 0, 1, 3, 4, 5, 100, int.MaxValue })
            {
                var output = new byte[5];
                ByteShuffle.Shuffle(data, 0, output, 0, 5, stride);
                bool identity = stride <= 1 || 5 / stride < 2;
                if (identity) Assert.SequenceEqual(data, output, "stride " + stride);
            }
        }
    }
}
