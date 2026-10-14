using System;

namespace SkylineTogether.Core.Hashing
{
    /// <summary>
    /// XXH3 128-bit (ARCHITECTURE.md#blob-transfer) with seed 0 and the default secret, transcribed from the algorithm of the
    /// reference xxHash library (one-shot paths for 0-16, 17-128, 129-240 bytes and the striped long-input path).
    /// Cross-checked in the tests against Microsoft's System.IO.Hashing implementation when it is available.
    /// </summary>
    internal static unsafe class Xxh3
    {
        internal const int StripeLength = 64;
        internal const int SecretConsumeRate = 8;
        internal const int SecretSize = 192;
        internal const int SecretLimit = SecretSize - StripeLength;                          // 128
        internal const int StripesPerBlock = (SecretSize - StripeLength) / SecretConsumeRate; // 16
        internal const int BlockLength = StripeLength * StripesPerBlock;                      // 1024
        internal const int MidSizeMax = 240;
        internal const int InternalBufferSize = 256;
        internal const int InternalBufferStripes = InternalBufferSize / StripeLength;          // 4
        private const int SecretLastAccStart = 7;
        private const int SecretMergeAccsStart = 11;
        private const int MidSizeStartOffset = 3;
        private const int MidSizeLastOffset = 17;
        private const int SecretSizeMin = 136;

        private const uint Prime32_1 = 0x9E3779B1U;
        private const uint Prime32_2 = 0x85EBCA77U;
        private const uint Prime32_3 = 0xC2B2AE3DU;
        private const ulong Prime64_1 = 0x9E3779B185EBCA87UL;
        private const ulong Prime64_2 = 0xC2B2AE3D27D4EB4FUL;
        private const ulong Prime64_3 = 0x165667B19E3779F9UL;
        private const ulong Prime64_4 = 0x85EBCA77C2B2AE63UL;
        private const ulong Prime64_5 = 0x27D4EB2F165667C5UL;
        private const ulong PrimeMx1 = 0x165667919E3779F9UL;
        private const ulong PrimeMx2 = 0x9FB21C651E98DF25UL;

        /// <summary>The default 192-byte secret of XXH3.</summary>
        internal static readonly byte[] DefaultSecret =
        {
            0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
            0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
            0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
            0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
            0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
            0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
            0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
            0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
            0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
            0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
            0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
            0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
        };

        /// <summary>One-shot XXH3-128 (seed 0) of <paramref name="length"/> bytes at <paramref name="input"/>.</summary>
        internal static Digest128 Hash128(byte* input, int length)
        {
            fixed (byte* secret = DefaultSecret)
            {
                const ulong seed = 0;
                if (length <= 16) return Len0To16(input, length, secret, seed);
                if (length <= 128) return Len17To128(input, length, secret, seed);
                if (length <= MidSizeMax) return Len129To240(input, length, secret, seed);
                return HashLong(input, length, secret);
            }
        }

        internal static void InitAccumulators(ulong* acc)
        {
            acc[0] = Prime32_3;
            acc[1] = Prime64_1;
            acc[2] = Prime64_2;
            acc[3] = Prime64_3;
            acc[4] = Prime64_4;
            acc[5] = Prime32_2;
            acc[6] = Prime64_5;
            acc[7] = Prime32_1;
        }

        /// <summary>Accumulates whole stripes and scrambles at block ends (shared by the streaming hasher); returns the end pointer.</summary>
        internal static byte* ConsumeStripes(ulong* acc, ref int stripesSoFar, byte* input, int stripes, byte* secret)
        {
            byte* initialSecret = secret + stripesSoFar * SecretConsumeRate;
            if (stripes >= StripesPerBlock - stripesSoFar)
            {
                int stripesThisIteration = StripesPerBlock - stripesSoFar;
                do
                {
                    Accumulate(acc, input, initialSecret, stripesThisIteration);
                    Scramble(acc, secret + SecretLimit);
                    input += stripesThisIteration * StripeLength;
                    stripes -= stripesThisIteration;
                    stripesThisIteration = StripesPerBlock;
                    initialSecret = secret;
                }
                while (stripes >= StripesPerBlock);
                stripesSoFar = 0;
            }
            if (stripes > 0)
            {
                Accumulate(acc, input, initialSecret, stripes);
                input += stripes * StripeLength;
                stripesSoFar += stripes;
            }
            return input;
        }

        /// <summary>The last stripe of a long input (read at an offset of the secret, as the reference does).</summary>
        internal static void AccumulateLastStripe(ulong* acc, byte* lastStripe, byte* secret)
        {
            Accumulate512(acc, lastStripe, secret + SecretLimit - SecretLastAccStart);
        }

        internal static Digest128 MergeLong(ulong* acc, byte* secret, ulong totalLength)
        {
            unchecked
            {
                return new Digest128
                {
                    Lo = MergeAccumulators(acc, secret + SecretMergeAccsStart, totalLength * Prime64_1),
                    Hi = MergeAccumulators(acc, secret + SecretSize - 64 - SecretMergeAccsStart, ~(totalLength * Prime64_2))
                };
            }
        }

        private static Digest128 HashLong(byte* input, int length, byte* secret)
        {
            ulong* acc = stackalloc ulong[8];
            InitAccumulators(acc);
            int blocks = (length - 1) / BlockLength;
            for (int n = 0; n < blocks; n++)
            {
                Accumulate(acc, input + n * BlockLength, secret, StripesPerBlock);
                Scramble(acc, secret + SecretLimit);
            }
            int stripes = ((length - 1) - BlockLength * blocks) / StripeLength;
            Accumulate(acc, input + blocks * BlockLength, secret, stripes);
            AccumulateLastStripe(acc, input + length - StripeLength, secret);
            return MergeLong(acc, secret, (ulong)length);
        }

        private static Digest128 Len0To16(byte* input, int length, byte* secret, ulong seed)
        {
            if (length > 8) return Len9To16(input, length, secret, seed);
            if (length >= 4) return Len4To8(input, length, secret, seed);
            if (length > 0) return Len1To3(input, length, secret, seed);
            ulong bitflipLo = ReadLE64(secret + 64) ^ ReadLE64(secret + 72);
            ulong bitflipHi = ReadLE64(secret + 80) ^ ReadLE64(secret + 88);
            return new Digest128 { Lo = XxHash64.Avalanche(seed ^ bitflipLo), Hi = XxHash64.Avalanche(seed ^ bitflipHi) };
        }

        private static Digest128 Len1To3(byte* input, int length, byte* secret, ulong seed)
        {
            unchecked
            {
                byte c1 = input[0];
                byte c2 = input[length >> 1];
                byte c3 = input[length - 1];
                uint combinedLo = ((uint)c1 << 16) | ((uint)c2 << 24) | c3 | ((uint)length << 8);
                uint combinedHi = RotateLeft32(Swap32(combinedLo), 13);
                ulong bitflipLo = (ReadLE32(secret) ^ ReadLE32(secret + 4)) + seed;
                ulong bitflipHi = (ReadLE32(secret + 8) ^ ReadLE32(secret + 12)) - seed;
                return new Digest128
                {
                    Lo = XxHash64.Avalanche(combinedLo ^ bitflipLo),
                    Hi = XxHash64.Avalanche(combinedHi ^ bitflipHi)
                };
            }
        }

        private static Digest128 Len4To8(byte* input, int length, byte* secret, ulong seed)
        {
            unchecked
            {
                seed ^= (ulong)Swap32((uint)seed) << 32;
                uint inputLo = ReadLE32(input);
                uint inputHi = ReadLE32(input + length - 4);
                ulong input64 = inputLo + ((ulong)inputHi << 32);
                ulong bitflip = (ReadLE64(secret + 16) ^ ReadLE64(secret + 24)) + seed;
                ulong keyed = input64 ^ bitflip;
                // shifting the length left keeps the multiplier odd-safe (as the reference does)
                ulong lo = Multiply128(keyed, Prime64_1 + ((ulong)length << 2), out ulong hi);
                hi += lo << 1;
                lo ^= hi >> 3;
                lo ^= lo >> 35;
                lo *= PrimeMx2;
                lo ^= lo >> 28;
                return new Digest128 { Lo = lo, Hi = Avalanche(hi) };
            }
        }

        private static Digest128 Len9To16(byte* input, int length, byte* secret, ulong seed)
        {
            unchecked
            {
                ulong bitflipLo = (ReadLE64(secret + 32) ^ ReadLE64(secret + 40)) - seed;
                ulong bitflipHi = (ReadLE64(secret + 48) ^ ReadLE64(secret + 56)) + seed;
                ulong inputLo = ReadLE64(input);
                ulong inputHi = ReadLE64(input + length - 8);
                ulong mLo = Multiply128(inputLo ^ inputHi ^ bitflipLo, Prime64_1, out ulong mHi);
                // put the length into the middle of the 128-bit value so it reaches both halves below
                mLo += (ulong)(length - 1) << 54;
                inputHi ^= bitflipHi;
                mHi += inputHi + (ulong)(uint)inputHi * (Prime32_2 - 1);
                mLo ^= Swap64(mHi);
                ulong hLo = Multiply128(mLo, Prime64_2, out ulong hHi);
                hHi += mHi * Prime64_2;
                return new Digest128 { Lo = Avalanche(hLo), Hi = Avalanche(hHi) };
            }
        }

        private static Digest128 Len17To128(byte* input, int length, byte* secret, ulong seed)
        {
            unchecked
            {
                ulong accLo = (ulong)length * Prime64_1;
                ulong accHi = 0;
                if (length > 32)
                {
                    if (length > 64)
                    {
                        if (length > 96)
                        {
                            Mix32(ref accLo, ref accHi, input + 48, input + length - 64, secret + 96, seed);
                        }
                        Mix32(ref accLo, ref accHi, input + 32, input + length - 48, secret + 64, seed);
                    }
                    Mix32(ref accLo, ref accHi, input + 16, input + length - 32, secret + 32, seed);
                }
                Mix32(ref accLo, ref accHi, input, input + length - 16, secret, seed);
                return FinishMid(accLo, accHi, length, seed);
            }
        }

        private static Digest128 Len129To240(byte* input, int length, byte* secret, ulong seed)
        {
            unchecked
            {
                ulong accLo = (ulong)length * Prime64_1;
                ulong accHi = 0;
                for (int i = 32; i < 160; i += 32)
                {
                    Mix32(ref accLo, ref accHi, input + i - 32, input + i - 16, secret + i - 32, seed);
                }
                accLo = Avalanche(accLo);
                accHi = Avalanche(accHi);
                // "i <= length" repeats the last 32 bytes when length % 32 == 0, as the reference does
                for (int i = 160; i <= length; i += 32)
                {
                    Mix32(ref accLo, ref accHi, input + i - 32, input + i - 16, secret + MidSizeStartOffset + i - 160, seed);
                }
                Mix32(ref accLo, ref accHi, input + length - 16, input + length - 32,
                      secret + SecretSizeMin - MidSizeLastOffset - 16, 0UL - seed);
                return FinishMid(accLo, accHi, length, seed);
            }
        }

        private static Digest128 FinishMid(ulong accLo, ulong accHi, int length, ulong seed)
        {
            unchecked
            {
                ulong lo = accLo + accHi;
                ulong hi = accLo * Prime64_1 + accHi * Prime64_4 + ((ulong)length - seed) * Prime64_2;
                return new Digest128 { Lo = Avalanche(lo), Hi = 0UL - Avalanche(hi) };
            }
        }

        private static void Mix32(ref ulong accLo, ref ulong accHi, byte* input1, byte* input2, byte* secret, ulong seed)
        {
            unchecked
            {
                accLo += Mix16(input1, secret, seed);
                accLo ^= ReadLE64(input2) + ReadLE64(input2 + 8);
                accHi += Mix16(input2, secret + 16, seed);
                accHi ^= ReadLE64(input1) + ReadLE64(input1 + 8);
            }
        }

        private static ulong Mix16(byte* input, byte* secret, ulong seed)
        {
            unchecked
            {
                return MultiplyFold64(ReadLE64(input) ^ (ReadLE64(secret) + seed), ReadLE64(input + 8) ^ (ReadLE64(secret + 8) - seed));
            }
        }

        private static void Accumulate(ulong* acc, byte* input, byte* secret, int stripes)
        {
            for (int n = 0; n < stripes; n++)
            {
                Accumulate512(acc, input + n * StripeLength, secret + n * SecretConsumeRate);
            }
        }

        private static void Accumulate512(ulong* acc, byte* input, byte* secret)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    ulong dataValue = ReadLE64(input + 8 * i);
                    ulong dataKey = dataValue ^ ReadLE64(secret + 8 * i);
                    acc[i ^ 1] += dataValue;                                // swap adjacent lanes
                    acc[i] += (ulong)(uint)dataKey * (dataKey >> 32);
                }
            }
        }

        private static void Scramble(ulong* acc, byte* secret)
        {
            unchecked
            {
                for (int i = 0; i < 8; i++)
                {
                    ulong a = acc[i];
                    a ^= a >> 47;
                    a ^= ReadLE64(secret + 8 * i);
                    a *= Prime32_1;
                    acc[i] = a;
                }
            }
        }

        private static ulong MergeAccumulators(ulong* acc, byte* secret, ulong start)
        {
            unchecked
            {
                ulong result = start;
                for (int i = 0; i < 4; i++)
                {
                    result += MultiplyFold64(acc[2 * i] ^ ReadLE64(secret + 16 * i), acc[2 * i + 1] ^ ReadLE64(secret + 16 * i + 8));
                }
                return Avalanche(result);
            }
        }

        private static ulong Avalanche(ulong h)
        {
            unchecked
            {
                h ^= h >> 37;
                h *= PrimeMx1;
                h ^= h >> 32;
                return h;
            }
        }

        private static ulong MultiplyFold64(ulong a, ulong b)
        {
            ulong lo = Multiply128(a, b, out ulong hi);
            return lo ^ hi;
        }

        /// <summary>Full 64 x 64 -> 128-bit product (portable: Math.BigMul for ulong is not in netstandard2.0).</summary>
        private static ulong Multiply128(ulong a, ulong b, out ulong hi)
        {
            unchecked
            {
                ulong aLo = (uint)a, aHi = a >> 32, bLo = (uint)b, bHi = b >> 32;
                ulong loLo = aLo * bLo;
                ulong hiLo = aHi * bLo;
                ulong loHi = aLo * bHi;
                ulong hiHi = aHi * bHi;
                ulong cross = (loLo >> 32) + (uint)hiLo + loHi;
                hi = (hiLo >> 32) + (cross >> 32) + hiHi;
                return (cross << 32) | (uint)loLo;
            }
        }

        private static uint Swap32(uint x)
        {
            return (x << 24) | ((x << 8) & 0x00FF0000U) | ((x >> 8) & 0x0000FF00U) | (x >> 24);
        }

        private static ulong Swap64(ulong x)
        {
            return ((ulong)Swap32((uint)x) << 32) | Swap32((uint)(x >> 32));
        }

        private static uint RotateLeft32(uint x, int bits) => (x << bits) | (x >> (32 - bits));

        private static ulong ReadLE64(byte* p) => XxHash64.ReadLE64(p);

        private static uint ReadLE32(byte* p) => XxHash64.ReadLE32(p);
    }
}
