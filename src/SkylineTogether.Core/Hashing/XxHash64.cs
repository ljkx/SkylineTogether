using System;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// xxHash64 (XXH64 of the reference xxHash library), Core's own implementation (no package).
    /// Used for entity value hashes (<c>EntityCodec</c>), change detection of channel payloads, tile hashes and blob chunk
    /// hashes. Output is identical on every platform: input is read little-endian.
    /// </summary>
    [ThreadAffinity(ThreadKind.Any)]
    public static class XxHash64
    {
        internal const ulong Prime1 = 0x9E3779B185EBCA87UL;
        internal const ulong Prime2 = 0xC2B2AE3D27D4EB4FUL;
        internal const ulong Prime3 = 0x165667B19E3779F9UL;
        internal const ulong Prime4 = 0x85EBCA77C2B2AE63UL;
        internal const ulong Prime5 = 0x27D4EB2F165667C5UL;

        public static unsafe ulong Hash(byte[] data, int offset, int count, ulong seed = 0)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "the range lies outside the array");
            }
            if (count == 0) return Hash((byte*)null, 0, seed);
            fixed (byte* p = &data[offset])
            {
                return Hash(p, count, seed);
            }
        }

        /// <summary>Hashes <paramref name="length"/> bytes at <paramref name="data"/> (may be null when the length is 0).</summary>
        public static unsafe ulong Hash(byte* data, int length, ulong seed = 0)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (data == null && length != 0) throw new ArgumentNullException(nameof(data));
            byte* p = data;
            byte* end = data + length;
            ulong h;
            if (length >= 32)
            {
                ulong v1 = unchecked(seed + Prime1 + Prime2);
                ulong v2 = unchecked(seed + Prime2);
                ulong v3 = seed;
                ulong v4 = unchecked(seed - Prime1);
                byte* limit = end - 32;
                do
                {
                    v1 = Round(v1, ReadLE64(p));
                    v2 = Round(v2, ReadLE64(p + 8));
                    v3 = Round(v3, ReadLE64(p + 16));
                    v4 = Round(v4, ReadLE64(p + 24));
                    p += 32;
                }
                while (p <= limit);
                h = MergeAccumulators(v1, v2, v3, v4);
            }
            else
            {
                h = unchecked(seed + Prime5);
            }
            h = unchecked(h + (ulong)length);
            return Finalize(h, p, (int)(end - p));
        }

        internal static ulong Round(ulong acc, ulong input)
        {
            unchecked
            {
                acc += input * Prime2;
                acc = RotateLeft(acc, 31);
                return acc * Prime1;
            }
        }

        internal static ulong MergeAccumulators(ulong v1, ulong v2, ulong v3, ulong v4)
        {
            unchecked
            {
                ulong h = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
                h = MergeRound(h, v1);
                h = MergeRound(h, v2);
                h = MergeRound(h, v3);
                return MergeRound(h, v4);
            }
        }

        /// <summary>Consumes the last 0-31 bytes and applies the avalanche.</summary>
        internal static unsafe ulong Finalize(ulong h, byte* p, int remaining)
        {
            unchecked
            {
                while (remaining >= 8)
                {
                    h ^= Round(0, ReadLE64(p));
                    h = RotateLeft(h, 27) * Prime1 + Prime4;
                    p += 8;
                    remaining -= 8;
                }
                if (remaining >= 4)
                {
                    h ^= ReadLE32(p) * Prime1;
                    h = RotateLeft(h, 23) * Prime2 + Prime3;
                    p += 4;
                    remaining -= 4;
                }
                while (remaining > 0)
                {
                    h ^= *p * Prime5;
                    h = RotateLeft(h, 11) * Prime1;
                    p++;
                    remaining--;
                }
                return Avalanche(h);
            }
        }

        internal static ulong Avalanche(ulong h)
        {
            unchecked
            {
                h ^= h >> 33;
                h *= Prime2;
                h ^= h >> 29;
                h *= Prime3;
                h ^= h >> 32;
                return h;
            }
        }

        private static ulong MergeRound(ulong acc, ulong value)
        {
            unchecked
            {
                acc ^= Round(0, value);
                return acc * Prime1 + Prime4;
            }
        }

        internal static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));

        internal static unsafe ulong ReadLE64(byte* p)
        {
            if (BitConverter.IsLittleEndian) return *(ulong*)p;
            return p[0] | ((ulong)p[1] << 8) | ((ulong)p[2] << 16) | ((ulong)p[3] << 24)
                   | ((ulong)p[4] << 32) | ((ulong)p[5] << 40) | ((ulong)p[6] << 48) | ((ulong)p[7] << 56);
        }

        internal static unsafe uint ReadLE32(byte* p)
        {
            if (BitConverter.IsLittleEndian) return *(uint*)p;
            return p[0] | ((uint)p[1] << 8) | ((uint)p[2] << 16) | ((uint)p[3] << 24);
        }
    }
}
