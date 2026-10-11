using System.Collections.Generic;
using SkylineTogether.Core.Hashing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Hashing
{
    /// <summary>HashMix: fixed definitions (part of the wire contract) and mixing quality.</summary>
    public sealed class HashMixTests
    {
        private static ulong ReferenceSplitMix64Output(ulong state)
        {
            // the SplitMix64 generator's next() written out independently: state += golden; mix(state)
            unchecked
            {
                ulong z = state + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        [Test]
        public void Mix64IsTheSplitMix64Output()
        {
            // SplitMix64 seeded with 0 starts with 0xE220A8397B1DCDAF (the published first output)
            Assert.Equal(0xE220A8397B1DCDAFUL, HashMix.Mix64(0));
            foreach (ulong x in new ulong[] { 1, 2, 0xDEADBEEF, ulong.MaxValue, 0x9E3779B97F4A7C15UL })
            {
                Assert.Equal(ReferenceSplitMix64Output(x), HashMix.Mix64(x));
            }
        }

        [Test]
        public void Mix64HasNoCollisionsOnASample()
        {
            var seen = new HashSet<ulong>();
            for (ulong i = 0; i < 200000; i++)
            {
                Assert.True(seen.Add(HashMix.Mix64(i)), "collision at " + i);
            }
        }

        [Test]
        public void Mix64Avalanches()
        {
            // flipping one input bit flips about half of the output bits
            int totalFlips = 0;
            int samples = 0;
            for (ulong x = 1; x < 2000; x += 7)
            {
                for (int bit = 0; bit < 64; bit++)
                {
                    ulong diff = HashMix.Mix64(x) ^ HashMix.Mix64(x ^ (1UL << bit));
                    totalFlips += PopCount(diff);
                    samples++;
                }
            }
            double average = (double)totalFlips / samples;
            Assert.InRange(average, 30, 34);
        }

        [Test]
        public void CombineIsDefinedAndOrderDependent()
        {
            Assert.Equal(HashMix.Mix64(unchecked(5 * 0xC2B2AE3D27D4EB4FUL + 9)), HashMix.Combine(5, 9));
            Assert.NotEqual(HashMix.Combine(1, 2), HashMix.Combine(2, 1));
            Assert.NotEqual(HashMix.Combine(0, 0), HashMix.Combine(0, 1));
        }

        [Test]
        public void Fold32XorsTheHalves()
        {
            Assert.Equal(0u, HashMix.Fold32(0));
            Assert.Equal(0x12345678u ^ 0x9ABCDEF0u, HashMix.Fold32(0x9ABCDEF012345678UL));
            Assert.Equal(0u, HashMix.Fold32(0xFFFFFFFFFFFFFFFFUL));
        }

        private static int PopCount(ulong v)
        {
            int count = 0;
            while (v != 0)
            {
                v &= v - 1;
                count++;
            }
            return count;
        }
    }
}
