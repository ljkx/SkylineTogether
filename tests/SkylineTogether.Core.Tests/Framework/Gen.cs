using System;
using System.Collections.Generic;
using System.Text;
using SkylineTogether.Core.Testing;

namespace SkylineTogether.Core.Tests.Framework
{
    /// <summary>
    /// A tiny generator for property-based tests on top of <see cref="DeterministicRandom"/>: every value comes from a
    /// fixed seed, so a failure is reproduced by its seed and iteration (printed by <see cref="Prop.ForAll"/>). Sizes and
    /// numbers lean towards the interesting cases (0, 1, the limits, varint boundaries) instead of being uniform.
    /// </summary>
    public sealed class Gen
    {
        private static readonly uint[] VarintEdges = { 0, 1, 0x7F, 0x80, 0x3FFF, 0x4000, 0x1FFFFF, 0x200000, 0xFFFFFFF, 0x10000000, uint.MaxValue };
        private static readonly byte[] ByteEdges = { 0x00, 0x01, 0x7F, 0x80, 0x81, 0xFE, 0xFF };

        public Gen(ulong seed)
        {
            Seed = seed;
            Random = new DeterministicRandom(seed);
        }

        public ulong Seed { get; }

        public DeterministicRandom Random { get; }

        public int Int(int maxExclusive) => Random.Next(maxExclusive);

        public int Int(int minInclusive, int maxExclusive) => Random.Next(minInclusive, maxExclusive);

        public bool Bool() => Random.NextBool();

        /// <summary>True with probability 1/<paramref name="n"/>.</summary>
        public bool OneIn(int n) => Random.Next(n) == 0;

        public double Double() => Random.NextDouble();

        public uint UInt() => Random.NextUInt();

        public ulong ULong() => Random.NextULong();

        public long Long() => (long)Random.NextULong();

        /// <summary>A uint that is a varint boundary a quarter of the time, small a quarter of the time, else uniform.</summary>
        public uint UIntEdgy()
        {
            switch (Random.Next(4))
            {
                case 0: return VarintEdges[Random.Next(VarintEdges.Length)];
                case 1: return (uint)Random.Next(300);
                default: return Random.NextUInt();
            }
        }

        /// <summary>A length in [0, max]: often 0, 1, max − 1 or max, otherwise small or uniform.</summary>
        public int Size(int max)
        {
            if (max <= 0) return 0;
            switch (Random.Next(8))
            {
                case 0: return 0;
                case 1: return Math.Min(1, max);
                case 2: return max;
                case 3: return Math.Max(0, max - 1);
                case 4:
                case 5: return Random.Next(Math.Min(max, 16) + 1);
                default: return Random.Next(max + 1);
            }
        }

        /// <summary>A finite float of any magnitude (or, when allowed, NaN and the infinities now and then).</summary>
        public float Float(bool allowNonFinite = false)
        {
            if (allowNonFinite && OneIn(16)) return Pick(float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            switch (Random.Next(4))
            {
                case 0: return Pick(0f, -0f, 1f, -1f, float.MaxValue, float.MinValue, float.Epsilon);
                case 1: return (float)(Random.NextDouble() * 2 - 1);
                default:
                    float f = BitConverter.Int32BitsToSingle((int)Random.NextUInt());
                    return float.IsNaN(f) || float.IsInfinity(f) ? 0.5f : f;
            }
        }

        public byte[] Bytes(int maxLength)
        {
            var b = new byte[Size(maxLength)];
            Random.NextBytes(b, 0, b.Length);
            return b;
        }

        public T Pick<T>(params T[] items) => items[Random.Next(items.Length)];

        public T Element<T>(IReadOnlyList<T> items) => items[Random.Next(items.Count)];

        /// <summary>A defined value of an enum.</summary>
        public T Enum<T>() where T : struct, Enum
        {
            Array values = System.Enum.GetValues(typeof(T));
            return (T)values.GetValue(Random.Next(values.Length));
        }

        public List<T> List<T>(int maxCount, Func<Gen, T> item)
        {
            int n = Size(maxCount);
            var list = new List<T>(n);
            for (int i = 0; i < n; i++) list.Add(item(this));
            return list;
        }

        /// <summary>
        /// A string of up to <paramref name="maxChars"/> UTF-16 code units mixing ASCII, accented Latin, CJK, emoji
        /// (surrogate pairs) and, when <paramref name="hostile"/>, control characters, bidi overrides, zero-width
        /// characters and lone surrogates.
        /// </summary>
        public string String(int maxChars, bool hostile = false)
        {
            int n = Size(maxChars);
            var sb = new StringBuilder(n);
            while (sb.Length < n)
            {
                switch (Random.Next(hostile ? 9 : 5))
                {
                    case 0:
                    case 1: sb.Append((char)Random.Next(0x20, 0x7F)); break;
                    case 2: sb.Append((char)Random.Next(0xC0, 0x180)); break;
                    case 3: sb.Append((char)Random.Next(0x4E00, 0x9FA0)); break;
                    case 4:
                        if (sb.Length + 2 <= n) sb.Append(char.ConvertFromUtf32(Random.Next(0x1F300, 0x1FA00)));
                        else sb.Append('x');
                        break;
                    case 5: sb.Append((char)Random.Next(0, 0x20)); break;
                    case 6: sb.Append((char)Pick(0x202E, 0x202D, 0x200B, 0x200F, 0xFEFF, 0x2028, 0x007F, 0x0085)); break;
                    case 7: sb.Append((char)Random.Next(0xD800, 0xE000)); break;           // lone surrogate
                    default: sb.Append(Pick(' ', '\t', '"', '\\', '<', '>', '&', '\'')); break;
                }
            }
            if (sb.Length > n) sb.Length = n;
            return sb.ToString();
        }

        /// <summary>
        /// A mutated copy of <paramref name="seed"/>: one to four rounds of bit flips, random or boundary bytes,
        /// truncation, insertion, removal, slice duplication or a garbage tail; now and then pure garbage.
        /// </summary>
        public byte[] Mutate(byte[] seed)
        {
            var data = new List<byte>(seed);
            int rounds = 1 + Random.Next(4);
            for (int r = 0; r < rounds; r++)
            {
                switch (Random.Next(9))
                {
                    case 0:
                        if (data.Count > 0)
                        {
                            int i = Random.Next(data.Count);
                            data[i] = (byte)(data[i] ^ (1 << Random.Next(8)));
                        }
                        break;
                    case 1:
                        if (data.Count > 0) data[Random.Next(data.Count)] = (byte)Random.Next(256);
                        break;
                    case 2:
                        if (data.Count > 0) data[Random.Next(data.Count)] = ByteEdges[Random.Next(ByteEdges.Length)];
                        break;
                    case 3:
                        if (data.Count > 0)
                        {
                            int keep = Random.Next(data.Count);
                            data.RemoveRange(keep, data.Count - keep);
                        }
                        break;
                    case 4:
                    {
                        int at = Random.Next(data.Count + 1);
                        for (int k = 1 + Random.Next(4); k > 0; k--) data.Insert(at, (byte)Random.Next(256));
                        break;
                    }
                    case 5:
                        if (data.Count > 0) data.RemoveAt(Random.Next(data.Count));
                        break;
                    case 6:
                        if (data.Count > 1)
                        {
                            int from = Random.Next(data.Count);
                            int length = 1 + Random.Next(Math.Min(16, data.Count - from));
                            data.InsertRange(Random.Next(data.Count + 1), data.GetRange(from, length));
                        }
                        break;
                    case 7:
                        // a large varint (a length or count far beyond the data) at a random position
                        if (data.Count > 0)
                        {
                            int at = Random.Next(data.Count);
                            data[at] = 0xFF;
                            data.Insert(Math.Min(at + 1, data.Count), 0xFF);
                            data.Insert(Math.Min(at + 2, data.Count), 0x7F);
                        }
                        break;
                    default:
                        for (int k = Random.Next(8); k > 0; k--) data.Add((byte)Random.Next(256));
                        break;
                }
            }
            if (OneIn(50))
            {
                data.Clear();
                for (int k = Random.Next(64); k > 0; k--) data.Add((byte)Random.Next(256));
            }
            return data.ToArray();
        }
    }

    /// <summary>Runners for property-based tests (see <see cref="Gen"/>).</summary>
    public static class Prop
    {
        /// <summary>
        /// Runs <paramref name="property"/> <paramref name="iterations"/> times, each with a fresh <see cref="Gen"/>
        /// seeded from <paramref name="seed"/> and the iteration, so one failing case is reproducible on its own. A
        /// failure names the property, the seed and the iteration.
        /// </summary>
        public static void ForAll(string name, ulong seed, int iterations, Action<Gen> property)
        {
            for (int i = 0; i < iterations; i++)
            {
                ulong caseSeed = Mix(seed, (ulong)i);
                var gen = new Gen(caseSeed);
                try
                {
                    property(gen);
                }
                catch (SkipException)
                {
                    throw;
                }
                catch (AssertionException e)
                {
                    throw new AssertionException(name + " failed at iteration " + i + " (case seed 0x" + caseSeed.ToString("X16") + "): " + e.Message);
                }
                catch (Exception e)
                {
                    throw new AssertionException(name + " threw at iteration " + i + " (case seed 0x" + caseSeed.ToString("X16") + "): "
                                                 + e.GetType().Name + ": " + e.Message + Environment.NewLine + e.StackTrace);
                }
            }
        }

        /// <summary>SplitMix64 finaliser of seed and index (decorrelates neighbouring cases).</summary>
        public static ulong Mix(ulong seed, ulong index)
        {
            ulong z = seed + 0x9E3779B97F4A7C15UL * (index + 1);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// The contract of every decoder of untrusted bytes: it returns, or throws <see cref="DecodeException"/>; nothing else
    /// escapes, and it never allocates far more than its input justifies (a length or count field claiming megabytes
    /// must be checked against the bytes that are actually there before anything is allocated).
    /// </summary>
    public static class DecoderContract
    {
        /// <summary>
        /// Runs <paramref name="decode"/> on <paramref name="input"/>; true when it was accepted, false when it threw
        /// <see cref="DecodeException"/>. Fails on any other exception and when the call allocated more than
        /// <paramref name="bytesPerInputByte"/> × input length + <paramref name="fixedBytes"/> on this thread.
        /// </summary>
        public static bool Check(string what, byte[] input, Action<byte[]> decode, long bytesPerInputByte = 256, long fixedBytes = 256 * 1024)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool accepted;
            try
            {
                decode(input);
                accepted = true;
            }
            catch (DecodeException)
            {
                accepted = false;
            }
            catch (Exception e)
            {
                Assert.Fail(what + ": " + e.GetType().Name + " escaped the decoder: " + e.Message + Environment.NewLine
                            + "     input " + Assert.Format(input) + Environment.NewLine + e.StackTrace);
                return false;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            long budget = bytesPerInputByte * input.Length + fixedBytes;
            if (allocated > budget)
            {
                Assert.Fail(what + ": decoding " + input.Length + " bytes allocated " + allocated + " bytes (budget " + budget + ")"
                            + (accepted ? "" : " before rejecting it") + Environment.NewLine + "     input " + Assert.Format(input));
            }
            return accepted;
        }

        /// <summary>
        /// Mutation fuzzing of one decoder: every seed must decode, then <paramref name="iterations"/> mutations of the
        /// seeds must obey <see cref="Check"/>. Returns the number of mutations that were rejected.
        /// </summary>
        public static int Fuzz(string what, IReadOnlyList<byte[]> seeds, int iterations, ulong seed, Action<byte[]> decode,
                               long bytesPerInputByte = 256, long fixedBytes = 256 * 1024)
        {
            foreach (byte[] valid in seeds)
            {
                Assert.True(Check(what + " (valid seed)", valid, decode, bytesPerInputByte, fixedBytes), what + ": a valid seed must decode");
            }
            var gen = new Gen(seed);
            int rejected = 0;
            for (int i = 0; i < iterations; i++)
            {
                byte[] input = gen.Mutate(seeds[gen.Int(seeds.Count)]);
                if (!Check(what + " mutation " + i, input, decode, bytesPerInputByte, fixedBytes)) rejected++;
            }
            return rejected;
        }
    }
}
