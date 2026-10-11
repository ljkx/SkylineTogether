using System;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>The binary16 conversions behind the water velocity channels, checked against .NET's System.Half.</summary>
    public sealed class HalfFloatTests
    {
        [Test]
        public void EveryHalfConvertsLikeSystemHalf()
        {
            for (int bits = 0; bits < 65536; bits++)
            {
                var half = (ushort)bits;
                float expected = (float)BitConverter.UInt16BitsToHalf(half);
                float actual = HalfFloat.ToSingle(half);
                if (float.IsNaN(expected))
                {
                    Assert.True(float.IsNaN(actual), "NaN half 0x" + bits.ToString("x4"));
                    Assert.False(HalfFloat.IsFinite(half));
                    Assert.True(float.IsNaN(HalfFloat.ToSingle(HalfFloat.FromSingle(actual))), "NaN stays NaN");
                    continue;
                }
                Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual), "half 0x" + bits.ToString("x4"));
                Assert.Equal(half, HalfFloat.FromSingle(actual), "round trip of half 0x" + bits.ToString("x4"));
                Assert.Equal(!float.IsInfinity(expected), HalfFloat.IsFinite(half));
            }
        }

        [Test]
        public void RandomSinglesRoundLikeSystemHalf()
        {
            var random = new DeterministicRandom(16);
            for (int i = 0; i < 200000; i++)
            {
                float value;
                switch (i % 4)
                {
                    case 0: value = BitConverter.Int32BitsToSingle((int)random.NextUInt()); break;            // any bit pattern
                    case 1: value = (float)((random.NextDouble() - 0.5) * 140000); break;                     // around the half range
                    case 2: value = (float)((random.NextDouble() - 0.5) * 1e-4); break;                       // subnormal halves
                    default: value = (float)((random.NextDouble() - 0.5) * 50); break;                         // velocities
                }
                ushort expected = BitConverter.HalfToUInt16Bits((Half)value);
                ushort actual = HalfFloat.FromSingle(value);
                if (float.IsNaN(value))
                {
                    Assert.False(HalfFloat.IsFinite(actual));
                    Assert.True(float.IsNaN(HalfFloat.ToSingle(actual)));
                    continue;
                }
                Assert.Equal(expected, actual, "value " + value.ToString("R"));
            }
        }

        [Test]
        public void KnownValuesAndTies()
        {
            Assert.Equal((ushort)0x3C00, HalfFloat.FromSingle(1f));
            Assert.Equal((ushort)0xC000, HalfFloat.FromSingle(-2f));
            Assert.Equal((ushort)0x7BFF, HalfFloat.FromSingle(65504f));
            Assert.Equal((ushort)0x7BFF, HalfFloat.FromSingle(65519f));
            Assert.Equal((ushort)0x7C00, HalfFloat.FromSingle(65520f), "the midpoint to 2^16 rounds to infinity");
            Assert.Equal((ushort)0x0001, HalfFloat.FromSingle((float)Math.Pow(2, -24)), "smallest subnormal");
            Assert.Equal((ushort)0x0000, HalfFloat.FromSingle((float)Math.Pow(2, -25)), "half of the smallest subnormal ties to even (0)");
            Assert.Equal((ushort)0x0002, HalfFloat.FromSingle((float)(3 * Math.Pow(2, -25))), "1.5 subnormal steps tie to even (2)");
            Assert.Equal((ushort)0x3C00, HalfFloat.FromSingle(1f + (float)Math.Pow(2, -11)), "a tie rounds to the even mantissa");
            Assert.Equal((ushort)0x3C02, HalfFloat.FromSingle(1f + 3 * (float)Math.Pow(2, -11)), "a tie rounds up to the even mantissa");
            Assert.Equal((ushort)0x8000, HalfFloat.FromSingle(-0f));
            Assert.Equal((ushort)0x7C00, HalfFloat.FromSingle(float.PositiveInfinity));
            Assert.Equal((ushort)0xFC00, HalfFloat.FromSingle(float.NegativeInfinity));
            Assert.Equal((ushort)0x0400, HalfFloat.FromSingle((float)Math.Pow(2, -14)), "smallest normal");
            Assert.Equal(65504f, HalfFloat.ToSingle(0x7BFF));
            Assert.Equal((float)Math.Pow(2, -24), HalfFloat.ToSingle(0x0001));
        }
    }
}
