using System;

namespace SkylineTogether.Core.Testing
{
    /// <summary>
    /// A seeded pseudo-random generator whose sequence is identical on every runtime and platform (xoshiro256**,
    /// seeded through SplitMix64), for reproducible tests, fuzzing and simulations. Bounded integers are unbiased
    /// (Lemire's multiply-and-reject). Not thread-safe; not for security.
    /// </summary>
    public sealed class DeterministicRandom
    {
        private ulong _s0, _s1, _s2, _s3;

        public DeterministicRandom(ulong seed)
        {
            ulong x = seed;
            _s0 = SplitMix64(ref x);
            _s1 = SplitMix64(ref x);
            _s2 = SplitMix64(ref x);
            _s3 = SplitMix64(ref x);
        }

        /// <summary>64 random bits.</summary>
        public ulong NextULong()
        {
            unchecked
            {
                ulong result = RotateLeft(_s1 * 5, 7) * 9;
                ulong t = _s1 << 17;
                _s2 ^= _s0;
                _s3 ^= _s1;
                _s1 ^= _s2;
                _s0 ^= _s3;
                _s2 ^= t;
                _s3 = RotateLeft(_s3, 45);
                return result;
            }
        }

        /// <summary>32 random bits (the high half of <see cref="NextULong"/>).</summary>
        public uint NextUInt() => (uint)(NextULong() >> 32);

        /// <summary>A uniform integer in [0, <paramref name="maxExclusive"/>); <paramref name="maxExclusive"/> must be positive.</summary>
        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive), "must be positive");
            return (int)NextBounded((uint)maxExclusive);
        }

        /// <summary>A uniform integer in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>); equal bounds return the bound.</summary>
        public int Next(int minInclusive, int maxExclusive)
        {
            if (maxExclusive < minInclusive) throw new ArgumentOutOfRangeException(nameof(maxExclusive), "must not be below minInclusive");
            if (maxExclusive == minInclusive) return minInclusive;
            uint range = (uint)((long)maxExclusive - minInclusive);
            return (int)(minInclusive + (long)NextBounded(range));
        }

        /// <summary>A uniform double in [0, 1) with 53 random bits.</summary>
        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

        public bool NextBool() => (NextULong() >> 63) != 0;

        /// <summary>Fills <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/>.</summary>
        public void NextBytes(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            int end = offset + count;
            while (offset < end)
            {
                ulong bits = NextULong();
                for (int i = 0; i < 8 && offset < end; i++)
                {
                    buffer[offset++] = (byte)bits;
                    bits >>= 8;
                }
            }
        }

        private uint NextBounded(uint range)
        {
            unchecked
            {
                ulong m = (ulong)NextUInt() * range;
                uint low = (uint)m;
                if (low < range)
                {
                    uint threshold = (0u - range) % range;
                    while (low < threshold)
                    {
                        m = (ulong)NextUInt() * range;
                        low = (uint)m;
                    }
                }
                return (uint)(m >> 32);
            }
        }

        private static ulong SplitMix64(ref ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
    }
}
