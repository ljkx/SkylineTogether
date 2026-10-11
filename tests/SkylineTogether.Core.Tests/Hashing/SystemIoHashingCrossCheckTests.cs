#if HAVE_SYSTEM_IO_HASHING
using System;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;
using Ms = System.IO.Hashing;

namespace SkylineTogether.Core.Tests.Hashing
{
    /// <summary>
    /// Bit-exact cross-check of Core's own xxHash64 and XXH3-128 against Microsoft's System.IO.Hashing, which the
    /// game ships in its Managed folder (compiled only when that DLL exists; see the test project file).
    /// </summary>
    public sealed class SystemIoHashingCrossCheckTests
    {
        private static ulong BigEndian64(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++) value = (value << 8) | bytes[offset + i];
            return value;
        }

        private static ulong ReferenceXxHash64(byte[] data, int length, ulong seed)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(data, 0, slice, 0, length);
            return BigEndian64(Ms.XxHash64.Hash(slice, unchecked((long)seed)), 0);
        }

        private static Digest128 ReferenceXxHash128(byte[] data, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(data, 0, slice, 0, length);
            byte[] canonical = Ms.XxHash128.Hash(slice);   // big-endian: high 64 bits, then low 64 bits
            return new Digest128(BigEndian64(canonical, 8), BigEndian64(canonical, 0));
        }

        [Test]
        public void XxHash64MatchesForEveryShortLengthAndSeeds()
        {
            var random = new DeterministicRandom(1);
            var data = new byte[600];
            random.NextBytes(data, 0, data.Length);
            ulong[] seeds = { 0, 1, 20141025, 0x9E3779B185EBCA87UL, ulong.MaxValue };
            for (int length = 0; length <= data.Length; length++)
            {
                foreach (ulong seed in seeds)
                {
                    Assert.Equal(ReferenceXxHash64(data, length, seed), XxHash64.Hash(data, 0, length, seed), "length " + length + " seed " + seed);
                }
            }
        }

        [Test]
        public void XxHash64MatchesForLongInputs()
        {
            var random = new DeterministicRandom(2);
            var data = new byte[200000];
            random.NextBytes(data, 0, data.Length);
            for (int round = 0; round < 40; round++)
            {
                int length = random.Next(data.Length + 1);
                ulong seed = random.NextULong();
                Assert.Equal(ReferenceXxHash64(data, length, seed), XxHash64.Hash(data, 0, length, seed), "length " + length);
            }
        }

        [Test]
        public void Xxh3For128BitsMatchesForEveryLengthUpToSeveralBlocks()
        {
            var random = new DeterministicRandom(3);
            var data = new byte[4500];
            random.NextBytes(data, 0, data.Length);
            for (int length = 0; length <= data.Length; length++)
            {
                Assert.Equal(ReferenceXxHash128(data, length), Digest128.Compute(data, 0, length), "length " + length);
            }
        }

        [Test]
        public void Xxh3For128BitsMatchesForLongInputsAndStreaming()
        {
            var random = new DeterministicRandom(4);
            var data = new byte[300000];
            random.NextBytes(data, 0, data.Length);
            for (int round = 0; round < 30; round++)
            {
                int length = random.Next(data.Length + 1);
                Digest128 reference = ReferenceXxHash128(data, length);
                Assert.Equal(reference, Digest128.Compute(data, 0, length), "one-shot length " + length);
                var stream = new XxHash128Stream();
                int position = 0;
                while (position < length)
                {
                    int piece = Math.Min(1 + random.Next(5000), length - position);
                    stream.Append(data, position, piece);
                    position += piece;
                }
                Assert.Equal(reference, stream.Finish(), "streamed length " + length);
            }
        }
    }
}
#endif
