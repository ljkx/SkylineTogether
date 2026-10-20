using System;
using SkylineTogether.Core.Motion;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Motion
{
    /// <summary>
    /// <see cref="MotionHalf"/> (the f16 velocities of followed movers) against .NET's <see cref="System.Half"/> as the
    /// oracle: every finite half value, both neighbours of every rounding midpoint and the midpoints themselves (ties
    /// round to the even mantissa), plus random singles. Beyond the binary16 range the codec clamps to ±65504 where
    /// System.Half gives infinity.
    /// </summary>
    public sealed class MotionHalfTests
    {
        private static ushort Expected(float value)
        {
            if (value > MotionHalf.MaxValue) return 0x7BFF;
            if (value < -MotionHalf.MaxValue) return 0xFBFF;
            return BitConverter.HalfToUInt16Bits((Half)value);
        }

        private static void Check(float value)
        {
            ushort expected = Expected(value);
            ushort actual = MotionHalf.FromSingle(value);
            if (actual != expected)
            {
                Assert.Fail("FromSingle(" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " = 0x"
                            + BitConverter.SingleToInt32Bits(value).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                            + "): expected 0x" + expected.ToString("x4", System.Globalization.CultureInfo.InvariantCulture)
                            + ", actual 0x" + actual.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        [Test]
        public void EveryHalfAndEveryRoundingMidpointMatchesSystemHalf()
        {
            for (int bits = 0; bits < 0x7C00; bits++)
            {
                foreach (int sign in new[] { 0, 0x8000 })
                {
                    float value = (float)BitConverter.UInt16BitsToHalf((ushort)(bits | sign));
                    Check(value);
                    if (bits == 0x7BFF) continue;
                    float next = (float)BitConverter.UInt16BitsToHalf((ushort)((bits + 1) | sign));
                    float midpoint = (value + next) / 2f; // exact: a float has 13 more mantissa bits than a half
                    Check(midpoint);
                    Check(MathF.BitDecrement(midpoint));
                    Check(MathF.BitIncrement(midpoint));
                }
            }
        }

        [Test]
        public void EveryHalfDecodesLikeSystemHalf()
        {
            for (int bits = 0; bits <= 0xFFFF; bits++)
            {
                var half = (ushort)bits;
                bool finite = (bits & 0x7C00) != 0x7C00;
                Assert.Equal(finite, MotionHalf.TryToSingle(half, out float value), "0x" + bits.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                if (!finite) continue;
                float expected = (float)BitConverter.UInt16BitsToHalf(half);
                Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(value), "0x" + bits.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        [Test]
        public void RandomSinglesAndTheClampMatch()
        {
            var random = new DeterministicRandom(4711);
            for (int i = 0; i < 200_000; i++)
            {
                // random bit patterns of finite singles, most of them inside the half range
                uint bits = (uint)random.Next(int.MaxValue) ^ ((uint)random.Next(2) << 31);
                float value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                if (i % 2 == 0) value = (float)(value % 70000.0);
                Check(value);
            }
            Check(65519.99f);
            Check(65520f);
            Check(-1e30f);
            Check(float.MaxValue);
        }
    }
}
