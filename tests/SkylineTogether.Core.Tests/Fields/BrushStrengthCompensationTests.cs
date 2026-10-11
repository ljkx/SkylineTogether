using System;
using SkylineTogether.Core.Fields;
using SkylineTogether.Core.Testing;
using SkylineTogether.Core.Tests.Framework;

namespace SkylineTogether.Core.Tests.Fields
{
    /// <summary>
    /// BrushStrengthCompensation against an independent transcription of the game's code
    /// (<c>TerrainSystem.ApplyToTerrain</c>, decompiled <c>TerrainSystem.cs:3765-3774</c>, with Unity.Mathematics semantics).
    /// </summary>
    public sealed class BrushStrengthCompensationTests
    {
        private const float HeightScale = 4096f;                                   // kDefaultHeightScaleOffset.x
        private static readonly float[] ToolStrengths = { 1000f, 2000f, 4000f };   // Soften, Shift, Level/Slope

        // ---- reference transcription of the game ----

        private static float GameAbs(float x) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) & 0x7FFFFFFF);

        private static float GameSign(float x) => (x > 0f ? 1f : 0f) - (x < 0f ? 1f : 0f);

        private static float GamePow(float x, float y) => (float)Math.Pow((double)x, (double)y);

        private static float GameMin(float x, float y) => float.IsNaN(y) || x < y ? x : y;

        private static float GameMax(float x, float y) => float.IsNaN(y) || x > y ? x : y;

        private static float GameClamp(float x, float a, float b) => GameMax(a, GameMin(b, x));

        /// <summary>The height change of one stamp in heightmap units (x2 of ApplyToTerrain).</summary>
        private static float GameHeightDelta(float strength, float delta, float toolBaseStrength)
        {
            float num = 0.002f;
            float num2 = strength;
            if (GameAbs(num2) < num)
            {
                float num3 = GameSign(num2);
                float x = GameAbs(num2) / num;
                float y = 0.055f;
                num2 = GamePow(x, y) * num * num3;
            }
            return GameClamp(delta, 0.05f, 1f) * num2 * toolBaseStrength / HeightScale;
        }

        private static float GameMapped(float strength) => GameHeightDelta(strength, 1f, HeightScale);   // clamp(1) · f(s) · 1

        /// <summary>Log-uniform strengths over [1e-30, 10] of both signs, plus the threshold neighbourhood.</summary>
        private static float[] Strengths(int count, ulong seed)
        {
            var random = new DeterministicRandom(seed);
            var values = new float[count + 8];
            for (int i = 0; i < count; i++)
            {
                double magnitude = Math.Pow(10, -30 + random.NextDouble() * 31);
                values[i] = (float)(random.NextBool() ? magnitude : -magnitude);
            }
            float t = BrushStrengthCompensation.LinearThreshold;
            float below = MathF.BitDecrement(t), above = MathF.BitIncrement(t);
            float[] edges = { t, -t, below, -below, above, -above, 1f, -1f };
            Array.Copy(edges, 0, values, count, edges.Length);
            return values;
        }

        // ---- Map ----

        [Test]
        public void MapIsTheIdentityAtAndAboveTheThreshold()
        {
            foreach (float s in new[] { 0.002f, 0.0021f, 0.01f, 0.5f, 1f, 3f, 1e30f, -0.002f, -0.7f, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Equal(s, BrushStrengthCompensation.Map(s), "strength " + s.ToString("R"));
            }
            Assert.True(float.IsNaN(BrushStrengthCompensation.Map(float.NaN)));
            Assert.Equal(0f, BrushStrengthCompensation.Map(0f));
        }

        [Test]
        public void MapMatchesTheGameBitForBit()
        {
            foreach (float s in Strengths(100000, 3765))
            {
                Assert.Equal(BitConverter.SingleToInt32Bits(GameMapped(s)), BitConverter.SingleToInt32Bits(BrushStrengthCompensation.Map(s)), "strength " + s.ToString("R"));
            }
            for (int bits = 1; bits < 1000; bits++)
            {
                float subnormal = BitConverter.Int32BitsToSingle(bits * 7919);
                Assert.Equal(GameMapped(subnormal), BrushStrengthCompensation.Map(subnormal), "subnormal " + subnormal.ToString("R"));
            }
        }

        [Test]
        public void MapIsMonotonicAndContinuousAtTheThreshold()
        {
            float previous = BrushStrengthCompensation.Map(1e-38f);
            for (double magnitude = 1e-38; magnitude < 10; magnitude *= 1.01)
            {
                float mapped = BrushStrengthCompensation.Map((float)magnitude);
                Assert.True(mapped >= previous, "monotonic at " + magnitude);
                previous = mapped;
            }
            float t = BrushStrengthCompensation.LinearThreshold;
            Assert.Near(t, BrushStrengthCompensation.Map(MathF.BitDecrement(t)), 1e-9, "continuous from below");
            Assert.True(BrushStrengthCompensation.Map(1e-30f) > 1e-5f, "tiny strengths are lifted by the 0.055 power");
        }

        // ---- Unmap ----

        [Test]
        public void UnmapInvertsMapOverTheWholeRange()
        {
            foreach (float s in Strengths(100000, 3766))
            {
                float roundTrip = BrushStrengthCompensation.Unmap(BrushStrengthCompensation.Map(s));
                if (Math.Abs(s) >= BrushStrengthCompensation.LinearThreshold)
                {
                    Assert.Equal(s, roundTrip, "identity region, strength " + s.ToString("R"));
                    continue;
                }
                // the inverse power 1/0.055 amplifies the float rounding of f(s) about 18 times
                Assert.True(Math.Abs(roundTrip - s) <= Math.Abs(s) * 6e-6, "strength " + s.ToString("R") + " → " + roundTrip.ToString("R"));
            }
        }

        [Test]
        public void UnmapStaysConsistentForSubnormalsAndSpecialValues()
        {
            for (int bits = 1; bits < 200000; bits += 37)
            {
                float s = BitConverter.Int32BitsToSingle(bits);   // positive subnormals
                float mapped = BrushStrengthCompensation.Map(s);
                float again = BrushStrengthCompensation.Map(BrushStrengthCompensation.Unmap(mapped));
                Assert.True(Math.Abs(again - mapped) <= mapped * 1e-6, "subnormal " + s.ToString("R"));
            }
            Assert.Equal(float.Epsilon, BrushStrengthCompensation.Unmap(1e-30f), "below the float range the smallest subnormal is used, never 0");
            Assert.Equal(-float.Epsilon, BrushStrengthCompensation.Unmap(-1e-30f));
            Assert.True(BrushStrengthCompensation.Unmap(BrushStrengthCompensation.Map(float.Epsilon)) > 0f);
            Assert.True(float.IsNaN(BrushStrengthCompensation.Unmap(float.NaN)));
            Assert.Equal(0f, BrushStrengthCompensation.Unmap(0f));
            Assert.Equal(float.PositiveInfinity, BrushStrengthCompensation.Unmap(float.PositiveInfinity));
            Assert.Equal(-0.5f, BrushStrengthCompensation.Unmap(-0.5f));
        }

        // ---- Compensate ----

        [Test]
        public void EqualFrameTimesLeaveTheStrengthUnchanged()
        {
            float[] times = { 0.001f, 1 / 144f, 1 / 60f, 0.05f, 1 / 15f, 0.3f, 1f, 2f };
            foreach (float s in Strengths(2000, 3767))
            {
                foreach (float dt in times)
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(s), BitConverter.SingleToInt32Bits(BrushStrengthCompensation.Compensate(s, dt, dt)), "strength " + s + " dt " + dt);
                }
                // both at 20 fps or faster: both clamp to 0.05
                Assert.Equal(s, BrushStrengthCompensation.Compensate(s, 1 / 30f, 1 / 144f));
                // both slower than 1 fps: both clamp to 1
                Assert.Equal(s, BrushStrengthCompensation.Compensate(s, 1.5f, 4f));
            }
        }

        [Test]
        public void FifteenAndSixtyFpsProduceEqualHeightDeltas()
        {
            AssertEqualHeights(1 / 15f, 1 / 60f, 3768);
            AssertEqualHeights(1 / 60f, 1 / 15f, 3769);
        }

        [Test]
        public void ExtremeFrameTimeRatiosProduceEqualHeightDeltas()
        {
            AssertEqualHeights(2f, 1 / 144f, 3770);    // ratio 20: the originator ran below 1 fps
            AssertEqualHeights(1 / 144f, 3f, 3771);    // ratio 1/20
            AssertEqualHeights(0.07f, 0.9f, 3772);
        }

        [Test]
        public void CompensationCrossesTheThresholdInBothDirections()
        {
            // 0.0019 · (0.0667 / 0.05) is above 0.002, so s′ lies in the linear region
            float up = BrushStrengthCompensation.Compensate(0.0019f, 1 / 15f, 1 / 60f);
            Assert.True(up >= BrushStrengthCompensation.LinearThreshold, "s′ = " + up);
            // 0.0025 · (0.05 / 0.0667) = 0.001875 is below 0.002, so s′ = 0.002 · 0.9375^(1/0.055) ≈ 0.000619
            float down = BrushStrengthCompensation.Compensate(0.0025f, 1 / 60f, 1 / 15f);
            Assert.Near(0.002 * Math.Pow(0.0025 * (0.05 / (1 / 15.0)) / 0.002, 1 / 0.055), down, 2e-8, "s′ in the power region");
            foreach (float tool in ToolStrengths)
            {
                AssertNear(GameHeightDelta(0.0019f, 1 / 15f, tool), GameHeightDelta(up, 1 / 60f, tool), "up, tool " + tool);
                AssertNear(GameHeightDelta(0.0025f, 1 / 60f, tool), GameHeightDelta(down, 1 / 15f, tool), "down, tool " + tool);
            }
        }

        [Test]
        public void InvalidAndZeroStrengths()
        {
            Assert.Equal(0f, BrushStrengthCompensation.Compensate(float.NaN, 1 / 15f, 1 / 60f));
            Assert.Equal(0f, BrushStrengthCompensation.Compensate(float.PositiveInfinity, 1 / 15f, 1 / 60f));
            Assert.Equal(0f, BrushStrengthCompensation.Compensate(float.NegativeInfinity, 1 / 15f, 1 / 60f));
            Assert.Equal(0f, BrushStrengthCompensation.Compensate(0f, 1 / 15f, 1 / 60f));
            Assert.Equal(float.MaxValue, BrushStrengthCompensation.Compensate(float.MaxValue, 1f, 1 / 60f), "results stay finite");
            Assert.Equal(-float.MaxValue, BrushStrengthCompensation.Compensate(-float.MaxValue, 1f, 1 / 60f));
            float tiny = BrushStrengthCompensation.Compensate(float.Epsilon, 1 / 60f, 1f);
            Assert.True(tiny > 0f, "a nonzero stamp never becomes a skipped zero stamp");
        }

        [Test]
        public void ClampDeltaMirrorsUnityMathematics()
        {
            Assert.Equal(1f, BrushStrengthCompensation.ClampDelta(float.NaN), "Unity's clamp turns NaN into the upper bound");
            Assert.Equal(1f, BrushStrengthCompensation.ClampDelta(float.PositiveInfinity));
            Assert.Equal(0.05f, BrushStrengthCompensation.ClampDelta(float.NegativeInfinity));
            Assert.Equal(0.05f, BrushStrengthCompensation.ClampDelta(0f));
            Assert.Equal(0.05f, BrushStrengthCompensation.ClampDelta(-1f));
            Assert.Equal(0.05f, BrushStrengthCompensation.ClampDelta(1 / 60f));
            Assert.Equal(0.5f, BrushStrengthCompensation.ClampDelta(0.5f));
            Assert.Equal(1f, BrushStrengthCompensation.ClampDelta(2f));
            foreach (float dt in new[] { float.NaN, -3f, 0f, 0.01f, 0.05f, 0.2f, 1f, 7f, float.PositiveInfinity, float.NegativeInfinity })
            {
                Assert.Equal(GameClamp(dt, 0.05f, 1f), BrushStrengthCompensation.ClampDelta(dt), "dt " + dt);
            }
            // a NaN frame time on either side behaves like the game's clamp (1)
            Assert.Equal(0.3f, BrushStrengthCompensation.Compensate(0.3f, float.NaN, 1f));
        }

        [Test]
        public void EffectiveStrengthIsTheGameFactor()
        {
            foreach (float s in Strengths(5000, 3773))
            {
                Assert.Equal(GameHeightDelta(s, 1 / 15f, HeightScale), BrushStrengthCompensation.EffectiveStrength(s, 1 / 15f));
                float compensated = BrushStrengthCompensation.Compensate(s, 1 / 15f, 1 / 60f);
                AssertNear(BrushStrengthCompensation.EffectiveStrength(s, 1 / 15f), BrushStrengthCompensation.EffectiveStrength(compensated, 1 / 60f), "strength " + s);
            }
        }

        private static void AssertEqualHeights(float originDeltaTime, float localDeltaTime, ulong seed)
        {
            double ratio = (double)BrushStrengthCompensation.ClampDelta(originDeltaTime) / BrushStrengthCompensation.ClampDelta(localDeltaTime);
            // Below this mapped value the float strength input is too coarse for a tight match: every nonzero strength
            // maps to at least f(smallest subnormal) ≈ 1.01e-5, and subnormal strengths have few significant bits. It
            // takes |s| below about 1e-17 to get there, far below anything the brush slider produces (s ≈ strength⁴).
            const double resolutionLimit = 2e-5;
            float[] strengths = Strengths(20000, seed);
            float[] typical = { 1e-6f, 1e-4f, 0.0019f, 0.002f, 0.0021f, 0.01f, 0.3f, 1f, -0.5f, -0.0001f, 2f };
            int tight = 0;
            foreach (float[] set in new[] { strengths, typical })
            {
                foreach (float s in set)
                {
                    float compensated = BrushStrengthCompensation.Compensate(s, originDeltaTime, localDeltaTime);
                    Assert.True(!float.IsNaN(compensated) && !float.IsInfinity(compensated), "finite s′ for " + s);
                    Assert.Equal(Math.Sign(s), Math.Sign(compensated), "the sign is kept for " + s);
                    double target = Math.Abs(BrushStrengthCompensation.Map(s)) * ratio;
                    foreach (float tool in ToolStrengths)
                    {
                        float expected = GameHeightDelta(s, originDeltaTime, tool);
                        float actual = GameHeightDelta(compensated, localDeltaTime, tool);
                        string what = "strength " + s.ToString("R") + " tool " + tool + " dt " + originDeltaTime + " → " + localDeltaTime;
                        if (target >= resolutionLimit)
                        {
                            AssertNear(expected, actual, what);
                            tight++;
                        }
                        else
                        {
                            // the error stays below one minimal nonzero effect of the game
                            double minimalEffect = 1.1e-5 * BrushStrengthCompensation.ClampDelta(localDeltaTime) * tool / HeightScale;
                            Assert.True(Math.Abs(expected - actual) <= minimalEffect, what + ": expected " + expected.ToString("R") + ", actual " + actual.ToString("R"));
                        }
                    }
                }
            }
            // the log-uniform sample spans 31 decades; even at the extreme ratio 1/20 over half of it is checked tightly
            Assert.True(tight > 3 * 8000, "strengths checked tightly: " + tight);
        }

        private static void AssertNear(float expected, float actual, string message)
        {
            double tolerance = Math.Abs(expected) * 2e-6 + 1e-30;
            Assert.True(Math.Abs(expected - actual) <= tolerance, message + ": expected " + expected.ToString("R") + ", actual " + actual.ToString("R"));
        }
    }
}
