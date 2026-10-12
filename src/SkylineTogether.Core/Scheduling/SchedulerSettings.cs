using System;

namespace SkylineTogether.Core.Scheduling
{
    /// <summary>
    /// An immutable, sanitised copy of a <see cref="SchedulerConfig"/>: every value is finite and inside its sensible
    /// range, falling back to the default when the configured value is not (a typo in a tuning file must degrade to the
    /// default, not to a scheduler that divides by zero). Taken once per consumer at construction, so it is safe to read
    /// from any thread.
    /// </summary>
    internal sealed class SchedulerSettings
    {
        private static readonly double[] DefaultQuanta = { 2, 4, 8, 16, 32 };

        // shares after P0 (normalised to sum 1) and the P1 floor
        public readonly double P1Share, P2Share, P3Share, MinP1ShareAfterP0, ReducedP3Share;
        public readonly int FloorBytesPerSecond, UnknownLinkBytesPerSecond, MinBurstBytes, MaxSlotVisitsPerFrame, MaxAttentionSetSize;
        public readonly double QueueHighMs, QueueLowMs, DecreaseFactor, IncreasePerSecond, SendRateFraction, DecreaseHoldSeconds;
        public readonly double[] PromotionQuanta;
        public readonly double P0OverloadSeconds, P0BacklogSeconds;
        public readonly float MinMotionRadius, MaxMotionRadius, MotionRadiusPerZoom, LeaveFactor;
        public readonly double BurstSeconds, MaxFrameSeconds, ChannelFirstFractionP1, ChannelReserveFractionP3, StaleRescanSeconds;
        public readonly double RollingWeightV, RollingWeightA, RollingWeightALow, RollingWeightOther;
        public readonly bool EstimateWireRatio;
        public readonly double MinWireRatio;
        public readonly bool UseFrustumFootprint;
        public readonly float FrustumHalfAngleRadians, FrustumVerticalHalfAngleRadians;
        public readonly double InViewRefreshSeconds;
        public readonly float InViewRefreshMoveFraction;
        public readonly double OverBudgetRatio, RecoverBudgetRatio, DegradeAfterSeconds, RecoverAfterSeconds, RadiusStepSeconds;
        public readonly float RadiusShrinkFactor, RadiusGrowFactor;

        /// <summary>The smallest radius scale the ladder applies: at it every zoom level yields the minimum radius.</summary>
        public readonly float MinRadiusScale;

        private SchedulerSettings(SchedulerConfig c)
        {
            double p1 = NonNegative(c.P1Share, 0.40), p2 = NonNegative(c.P2Share, 0.45), p3 = NonNegative(c.P3Share, 0.15);
            double sum = p1 + p2 + p3;
            if (!(sum > 1e-9)) { p1 = 0.40; p2 = 0.45; p3 = 0.15; sum = 1.0; }
            P1Share = p1 / sum;
            P2Share = p2 / sum;
            P3Share = p3 / sum;
            MinP1ShareAfterP0 = Range(c.MinP1ShareAfterP0, 0.0, 1.0, 0.30);
            ReducedP3Share = Range(c.ReducedP3Share, 0.0, 1.0, 0.05);

            FloorBytesPerSecond = c.FloorBytesPerSecond > 0 ? c.FloorBytesPerSecond : 48_000;
            UnknownLinkBytesPerSecond = c.UnknownLinkBytesPerSecond > 0 ? c.UnknownLinkBytesPerSecond : 2_000_000;
            MinBurstBytes = c.MinBurstBytes > 0 ? c.MinBurstBytes : 16 * 1024;
            MaxSlotVisitsPerFrame = c.MaxSlotVisitsPerFrame >= 64 ? c.MaxSlotVisitsPerFrame : 16384;
            MaxAttentionSetSize = c.MaxAttentionSetSize > 0 ? Math.Min(c.MaxAttentionSetSize, (int)NetSlot.MaxValue) : 1 << 20;

            QueueHighMs = Positive(c.QueueHighMs, 150);
            QueueLowMs = Range(c.QueueLowMs, 0.0, QueueHighMs, Math.Min(30, QueueHighMs));
            DecreaseFactor = Range(c.DecreaseFactor, 0.05, 0.999, 0.8);
            IncreasePerSecond = Range(c.IncreasePerSecond, 0.0, 10.0, 0.05);
            SendRateFraction = Range(c.SendRateFraction, 0.05, 1.0, 0.85);
            DecreaseHoldSeconds = Range(c.DecreaseHoldSeconds, 0.0, 60.0, 0.5);

            PromotionQuanta = SanitizeQuanta(c.PromotionQuantaSeconds);
            P0OverloadSeconds = Range(c.P0OverloadSeconds, 0.0, 3600.0, 30);
            P0BacklogSeconds = Range(c.P0BacklogSeconds, 0.0, 3600.0, 2);

            float minRadius = c.MinMotionRadius > 0 && IsFinite(c.MinMotionRadius) ? c.MinMotionRadius : 300f;
            float maxRadius = c.MaxMotionRadius > 0 && IsFinite(c.MaxMotionRadius) ? c.MaxMotionRadius : 2500f;
            if (maxRadius < minRadius) { minRadius = 300f; maxRadius = 2500f; }
            MinMotionRadius = minRadius;
            MaxMotionRadius = maxRadius;
            MotionRadiusPerZoom = c.MotionRadiusPerZoom > 0 && IsFinite(c.MotionRadiusPerZoom) ? c.MotionRadiusPerZoom : 1.0f;
            LeaveFactor = c.LeaveFactor >= 1f && c.LeaveFactor <= 10f ? c.LeaveFactor : 1.25f;
            MinRadiusScale = Math.Max(0.01f, Math.Min(1f, MinMotionRadius / MaxMotionRadius));

            BurstSeconds = Range(c.BurstSeconds, 0.01, 10.0, 0.25);
            MaxFrameSeconds = Range(c.MaxFrameSeconds, 0.01, 10.0, 0.5);
            ChannelFirstFractionP1 = Range(c.ChannelFirstFractionP1, 0.0, 1.0, 0.5);
            ChannelReserveFractionP3 = Range(c.ChannelReserveFractionP3, 0.0, 1.0, 0.2);
            StaleRescanSeconds = Range(c.StaleRescanSeconds, 0.0, 60.0, 0.25);

            double wv = NonNegative(c.RollingWeightV, 0.5), wa = NonNegative(c.RollingWeightA, 0.3);
            double wl = NonNegative(c.RollingWeightALow, 0.1), wo = NonNegative(c.RollingWeightOther, 0.1);
            double wsum = wv + wa + wl + wo;
            if (!(wsum > 1e-9)) { wv = 0.5; wa = 0.3; wl = 0.1; wo = 0.1; wsum = 1.0; }
            RollingWeightV = wv / wsum;
            RollingWeightA = wa / wsum;
            RollingWeightALow = wl / wsum;
            RollingWeightOther = wo / wsum;

            EstimateWireRatio = c.EstimateWireRatio;
            MinWireRatio = Range(c.MinWireRatio, 0.01, 1.0, 0.25);

            UseFrustumFootprint = c.UseFrustumFootprint;
            float half = c.FrustumHalfAngleDegrees > 0f && c.FrustumHalfAngleDegrees < 89f ? c.FrustumHalfAngleDegrees : 40f;
            float vhalf = c.FrustumVerticalHalfAngleDegrees > 0f && c.FrustumVerticalHalfAngleDegrees < 89f ? c.FrustumVerticalHalfAngleDegrees : 22.5f;
            FrustumHalfAngleRadians = (float)(half * Math.PI / 180.0);
            FrustumVerticalHalfAngleRadians = (float)(vhalf * Math.PI / 180.0);
            InViewRefreshSeconds = Range(c.InViewRefreshSeconds, 0.05, 3600.0, 1.0);
            InViewRefreshMoveFraction = c.InViewRefreshMoveFraction > 0f && c.InViewRefreshMoveFraction <= 10f ? c.InViewRefreshMoveFraction : 0.25f;

            OverBudgetRatio = Range(c.OverBudgetRatio, 1.0, 100.0, 1.05);
            RecoverBudgetRatio = Range(c.RecoverBudgetRatio, 0.0, 1.0, 0.55);
            DegradeAfterSeconds = Range(c.DegradeAfterSeconds, 0.0, 600.0, 1.0);
            RecoverAfterSeconds = Range(c.RecoverAfterSeconds, 0.0, 3600.0, 5.0);
            RadiusStepSeconds = Range(c.RadiusStepSeconds, 0.0, 600.0, 0.5);
            RadiusShrinkFactor = c.RadiusShrinkFactor > 0.05f && c.RadiusShrinkFactor < 1f ? c.RadiusShrinkFactor : 0.85f;
            RadiusGrowFactor = c.RadiusGrowFactor > 1f && c.RadiusGrowFactor < 10f ? c.RadiusGrowFactor : 1.1f;
        }

        /// <summary>The last promotion quantum: pages at least this old are promoted into P1 (after the attention classes)
        /// and inspection-closure values withheld for time alignment are released as a safety net.</summary>
        public double LastQuantum => PromotionQuanta[PromotionQuanta.Length - 1];

        /// <summary>The first promotion quantum: pages at least this old count as stale.</summary>
        public double FirstQuantum => PromotionQuanta[0];

        public static SchedulerSettings From(SchedulerConfig config) => new SchedulerSettings(config ?? new SchedulerConfig());

        /// <summary>Number of quanta a page of age <paramref name="ageSeconds"/> has passed (0 ... quanta count).</summary>
        public int PromotionLevel(double ageSeconds)
        {
            int level = 0;
            double[] quanta = PromotionQuanta;
            while (level < quanta.Length && ageSeconds >= quanta[level]) level++;
            return level;
        }

        internal static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        internal static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static double NonNegative(double v, double fallback) => IsFinite(v) && v >= 0 ? v : fallback;

        private static double Positive(double v, double fallback) => IsFinite(v) && v > 0 ? v : fallback;

        private static double Range(double v, double min, double max, double fallback) => IsFinite(v) && v >= min && v <= max ? v : fallback;

        private static double[] SanitizeQuanta(double[] quanta)
        {
            if (quanta == null || quanta.Length == 0 || quanta.Length > 16) return (double[])DefaultQuanta.Clone();
            double previous = 0;
            for (int i = 0; i < quanta.Length; i++)
            {
                double q = quanta[i];
                if (!IsFinite(q) || q <= previous || q > 86400) return (double[])DefaultQuanta.Clone();
                previous = q;
            }
            return (double[])quanta.Clone();
        }
    }
}
